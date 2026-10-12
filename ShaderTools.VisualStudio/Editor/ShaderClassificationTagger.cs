using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using ShaderTools.Core.Highlighting;
using ShaderTools.VisualStudio.LanguageClient;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace ShaderTools.VisualStudio.Editor
{
    [Export(typeof(IViewTaggerProvider)), ContentType(ShaderContentTypes.Glsl), ContentType(ShaderContentTypes.Hlsl)]
    [TextViewRole(PredefinedTextViewRoles.Document), TagType(typeof(ClassificationTag)), Name("ShaderTools.Classification")]
    internal sealed class ShaderClassificationTaggerProvider : IViewTaggerProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry { get; set; }
        [Import] internal IClassificationFormatMapService FormatMaps { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView view, ITextBuffer buffer) where T : ITag
        {
            if (!typeof(T).IsAssignableFrom(typeof(ClassificationTag)) || !ReferenceEquals(view.TextBuffer, buffer))
            {
                return null;
            }
            // Each aggregator owns and disposes its taggers. A view singleton can
            // be disposed by a temporary aggregator while the editor still uses it.
            return (ITagger<T>)(object)new ShaderClassificationTagger(
                view, buffer, Registry, FormatMaps.GetClassificationFormatMap(view));
        }
    }

    // Shader scopes do not have the same TextMate theme mappings as C++. Use
    // Visual Assist's palette when its classifications are available, then the
    // standard C++ classifications, without changing either extension's styles.
    internal sealed class ShaderClassificationTagger : ITagger<ClassificationTag>, IDisposable, IShaderOptionsListener
    {
        private readonly ITextView view;
        private readonly ITextBuffer buffer;
        private readonly IClassificationTypeRegistryService registry;
        private readonly IClassificationFormatMap formatMap;
        private readonly Dictionary<ShaderTokenKind, IClassificationType> types = [];
        private readonly object gate = new object();
        private CancellationTokenSource pending;
        private ITextSnapshot classifiedSnapshot;
        private IReadOnlyList<ShaderClassificationSpan> classified = Array.Empty<ShaderClassificationSpan>();
        private bool synchronizingColors;
        private bool disposed;

        internal ShaderClassificationTagger(ITextView view, ITextBuffer buffer,
            IClassificationTypeRegistryService registry, IClassificationFormatMap formatMap)
        {
            this.view = view;
            this.buffer = buffer;
            this.registry = registry;
            this.formatMap = formatMap;
            foreach (ShaderTokenKind kind in Enum.GetValues(typeof(ShaderTokenKind)))
            {
                string name = "ShaderTools.Syntax." + kind;
                types.Add(kind, registry.GetClassificationType(name) ??
                    registry.CreateClassificationType(name, Array.Empty<IClassificationType>()));
            }
            SynchronizeColors();
            formatMap.ClassificationFormatMappingChanged += OnColorsChanged;
            buffer.Changed += OnBufferChanged;
            buffer.ContentTypeChanged += OnContentTypeChanged;
            view.Closed += OnClosed;
            ShaderOptionChanges.Register(this);
            Schedule();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<ClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || !Options.ShaderOptions.Current.UseCppColors)
            {
                yield break;
            }

            IReadOnlyList<ShaderClassificationSpan> current;
            ITextSnapshot sourceSnapshot;
            lock (gate)
            {
                if (disposed || classifiedSnapshot == null)
                {
                    yield break;
                }

                current = classified;
                sourceSnapshot = classifiedSnapshot;
            }
            // The classifier returns sorted, disjoint spans. Binary search avoids
            // scanning the entire file for every visible line requested by VS.
            foreach (SnapshotSpan requested in spans)
            {
                SnapshotSpan original = requested.TranslateTo(sourceSnapshot, SpanTrackingMode.EdgeInclusive);
                int low = 0, high = current.Count;
                while (low < high)
                {
                    int mid = low + ((high - low) / 2);
                    if (current[mid].Start + current[mid].Length <= original.Start.Position)
                    {
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid;
                    }
                }
                for (int index = low; index < current.Count && current[index].Start < original.End.Position; index++)
                {
                    ShaderClassificationSpan token = current[index];
                    SnapshotSpan translated = new SnapshotSpan(sourceSnapshot, token.Start, token.Length)
                        .TranslateTo(requested.Snapshot, SpanTrackingMode.EdgeExclusive);
                    int start = Math.Max(translated.Start.Position, requested.Start.Position);
                    int end = Math.Min(translated.End.Position, requested.End.Position);
                    if (end > start)
                    {
                        yield return new TagSpan<ClassificationTag>(
                        new SnapshotSpan(requested.Snapshot, start, end - start), new ClassificationTag(types[token.Kind]));
                    }
                }
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            Schedule();
        }

        private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e) { Schedule(); RaiseChanged(); }
        public void OnShaderOptionsChanged() { Schedule(); RaiseChanged(); }
        private void OnColorsChanged(object sender, EventArgs e)
        {
            SynchronizeColors();
        }

        private void SynchronizeColors()
        {
            if (disposed || synchronizingColors)
            {
                return;
            }

            synchronizingColors = true;
            try
            {
                foreach (KeyValuePair<ShaderTokenKind, IClassificationType> entry in types)
                {
                    IClassificationType source = null;
                    foreach (string candidate in Sources(entry.Key))
                    {
                        if ((source = registry.GetClassificationType(candidate)) != null)
                        {
                            break;
                        }
                    }

                    if (source == null)
                    {
                        continue;
                    }

                    TextFormattingRunProperties style = formatMap.GetTextProperties(source);
                    bool registered = formatMap.CurrentPriorityOrder.Contains(entry.Value);
                    TextFormattingRunProperties previous = registered ? formatMap.GetExplicitTextProperties(entry.Value) : null;
                    if (previous != null && !previous.ForegroundBrushEmpty && previous.ForegroundBrushSame(style.ForegroundBrush) &&
                        !previous.BoldEmpty && previous.Bold == style.Bold && !previous.ItalicEmpty && previous.Italic == style.Italic)
                    {
                        continue;
                    }
                    // Only set color/bold/italic. Keeping opacity and background unset
                    // allows inactive-code fading, selection and error adornments to compose.
                    TextFormattingRunProperties properties = TextFormattingRunProperties.CreateTextFormattingRunProperties()
                        .SetForegroundBrush(style.ForegroundBrush).SetBold(style.Bold).SetItalic(style.Italic);
                    if (!registered)
                    {
                        formatMap.AddExplicitTextProperties(entry.Value, properties);
                    }
                    // AddExplicitTextProperties behaves like SetTextProperties for
                    // an existing entry and can drop values equal to the defaults.
                    // Keep those values explicit so lower-priority TextMate styles
                    // cannot leak through when another shader view shares this map.
                    formatMap.SetExplicitTextProperties(entry.Value, properties);
                }
            }
            finally { synchronizingColors = false; }
        }

        private static IEnumerable<string> Sources(ShaderTokenKind kind)
        {
            switch (kind)
            {
                case ShaderTokenKind.Function: yield return "vax_type_method"; yield return "cppFunction"; yield return "method name"; break;
                case ShaderTokenKind.Type: yield return "vax_type_class"; yield return "cppType"; yield return "class name"; break;
                case ShaderTokenKind.Namespace: yield return "vax_type_namespace"; yield return "cppNamespace"; yield return "namespace name"; break;
                case ShaderTokenKind.GlobalVariable: yield return "vax_type_var"; yield return "cppGlobalVariable"; yield return "field name"; break;
                case ShaderTokenKind.LocalVariable: yield return "vax_type_var"; yield return "cppLocalVariable"; yield return "local name"; break;
                case ShaderTokenKind.Parameter: yield return "vax_type_var"; yield return "cppParameter"; yield return "parameter name"; break;
                case ShaderTokenKind.Field: yield return "vax_type_var"; yield return "cppMemberField"; yield return "field name"; break;
                case ShaderTokenKind.Macro: yield return "vax_type_macro"; yield return "cppMacro"; yield return "preprocessor keyword"; break;
                case ShaderTokenKind.Semantic: yield return "vax_type_enum_member"; yield return "cppEnumerator"; yield return "enum member name"; break;
                case ShaderTokenKind.ControlKeyword: yield return "cppControlKeyword"; yield return "keyword"; break;
                case ShaderTokenKind.Keyword: yield return "keyword"; break;
                case ShaderTokenKind.PreprocessorKeyword: yield return "preprocessor keyword"; break;
                case ShaderTokenKind.Number: yield return "number"; break;
                case ShaderTokenKind.String: yield return "string"; break;
                case ShaderTokenKind.Comment: yield return "comment"; break;
            }
            yield return "identifier";
        }

        private void Schedule()
        {
            ITextSnapshot snapshot = buffer.CurrentSnapshot;
            bool hlsl = buffer.ContentType.IsOfType(ShaderContentTypes.Hlsl);
            bool enabled = Options.ShaderOptions.Current.UseCppColors &&
                (hlsl || buffer.ContentType.IsOfType(ShaderContentTypes.Glsl));
            CancellationTokenSource work;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                pending?.Cancel();
                pending = null;
                if (!enabled)
                {
                    classified = Array.Empty<ShaderClassificationSpan>();
                    classifiedSnapshot = null;
                    return;
                }
                pending = work = new CancellationTokenSource();
            }
            _ = RefreshAsync(snapshot, hlsl, work);
        }

        private async Task RefreshAsync(ITextSnapshot snapshot, bool hlsl, CancellationTokenSource work)
        {
            try
            {
                await Task.Delay(100, work.Token).ConfigureAwait(false);
                IReadOnlyList<ShaderClassificationSpan> result = await Task.Run(() => ShaderClassifier.Classify(snapshot.GetText(), hlsl, work.Token), work.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (disposed || !ReferenceEquals(pending, work) || snapshot != buffer.CurrentSnapshot)
                    {
                        return;
                    }

                    classified = result;
                    classifiedSnapshot = snapshot;
                }
                RaiseChanged();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Services.ShaderOutput.Log("Shader colors: " + ex.Message); }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(pending, work))
                    {
                        pending = null;
                    }
                }
                work.Dispose();
            }
        }

        private void RaiseChanged()
        {
            ShaderUiNotifications.Post(() =>
        {
            if (!disposed)
            {
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)));
            }
        });
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                pending?.Cancel();
                classified = Array.Empty<ShaderClassificationSpan>();
                classifiedSnapshot = null;
            }
            formatMap.ClassificationFormatMappingChanged -= OnColorsChanged;
            buffer.Changed -= OnBufferChanged;
            buffer.ContentTypeChanged -= OnContentTypeChanged;
            view.Closed -= OnClosed;
            ShaderOptionChanges.Unregister(this);
        }
    }
}
