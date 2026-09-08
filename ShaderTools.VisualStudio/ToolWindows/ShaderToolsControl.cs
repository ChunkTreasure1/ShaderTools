using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShaderTools.VisualStudio.Commands;
using ShaderTools.VisualStudio.LanguageClient;
using ShaderTools.VisualStudio.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using ShaderTools.Core.Configuration;

namespace ShaderTools.VisualStudio.ToolWindows
{
    internal sealed class ShaderToolsControl : UserControl, IDisposable
    {
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 12) };
        private readonly TextBlock active = new TextBlock { Text = "Automatic shader context", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8) };
        private readonly TreeView variants = new TreeView { Margin = new Thickness(8) };
        private readonly TreeView symbols = new TreeView { Margin = new Thickness(8) };
        private readonly TextBlock detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Opacity = 0.85 };
        private readonly TextBlock notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        private readonly TextBox search = new TextBox { MinWidth = 120, Margin = new Thickness(4), ToolTip = "Workspace symbol name; press Enter to search" };
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private ShaderVariant selected;
        private ShaderVariant activated => ShaderLanguageClient.Instance?.ActiveVariant;
        private string catalogPath;
        private string catalogRoot;
        private bool disposed;
        private bool attached;
        private long refreshVersion;

        public ShaderToolsControl()
        {
            SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            var root = new DockPanel();
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = "SHADER TOOLS", FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 12, 12, 0), Opacity = 0.7 });
            heading.Children.Add(status);
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            DockPanel.SetDock(notice, Dock.Bottom);
            root.Children.Add(notice);
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Variants", Content = BuildVariantPage() });
            tabs.Items.Add(new TabItem { Header = "Symbols", Content = BuildSymbolPage() });
            root.Children.Add(tabs);
            Content = root;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            variants.SelectedItemChanged += (_, __) =>
            {
                selected = (variants.SelectedItem as TreeViewItem)?.Tag as ShaderVariant;
                detail.Text = selected == null ? "" : selected.EntryPoint + " · " + (selected.Stage ?? "auto stage") + Environment.NewLine + selected.FilePath +
                    (selected.Defines.Count == 0 ? "" : Environment.NewLine + string.Join(", ", selected.Defines.Select(pair => pair.Key + "=" + pair.Value))) +
                    (selected.Includes.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, selected.Includes));
            };
            variants.MouseDoubleClick += (_, __) => Run(ActivateSelectedAsync);
            variants.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(ActivateSelectedAsync); e.Handled = true; } };
            symbols.MouseDoubleClick += (_, __) => Run(NavigateSymbolAsync);
            symbols.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(NavigateSymbolAsync); e.Handled = true; } };
        }

        private UIElement BuildVariantPage()
        {
            var panel = new DockPanel();
            var tools = new WrapPanel { Margin = new Thickness(8, 8, 8, 0) };
            tools.Children.Add(Button("Activate", "Use the selected entry point, stage and macros", ActivateSelectedAsync));
            tools.Children.Add(Button("Automatic", "Clear the pinned variant context", ClearVariantAsync));
            tools.Children.Add(Button("Refresh", "Reload the variant database", ReloadVariantsAsync));
            tools.Children.Add(Button("Edit…", "Create or edit project variants", () => ShaderCommands.EditProjectFileAsync(true)));
            tools.Children.Add(Button("Load…", "Load a Shader Tools or shader-validator variant database", LoadCatalogAsync));
            DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
            DockPanel.SetDock(active, Dock.Top); panel.Children.Add(active);
            DockPanel.SetDock(detail, Dock.Bottom); panel.Children.Add(detail);
            panel.Children.Add(variants);
            return panel;
        }

        private UIElement BuildSymbolPage()
        {
            var panel = new DockPanel();
            var tools = new WrapPanel { Margin = new Thickness(8) };
            tools.Children.Add(Button("Current file", "List symbols in the active shader", LoadDocumentSymbolsAsync));
            tools.Children.Add(search);
            tools.Children.Add(Button("Search", "Search symbols in the shader workspace", LoadWorkspaceSymbolsAsync));
            search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(LoadWorkspaceSymbolsAsync); e.Handled = true; } };
            DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools); panel.Children.Add(symbols);
            return panel;
        }

        private Button Button(string text, string tooltip, Func<Task> action)
        {
            var button = new Button { Content = text, ToolTip = tooltip, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), MinHeight = 26 };
            button.Click += (_, __) => Run(action);
            return button;
        }

        private void Run(Func<Task> action) => BackgroundWork.Run(async () =>
        {
            try { notice.Text = ""; await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); notice.Text = ex.Message; ShaderOutput.Log(ex.Message); }
        }, "ShaderTools/Explorer");

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!attached) { ShaderLanguageClient.ReadyChanged += OnStateChanged; attached = true; }
            UpdateStatus();
            Run(ReloadVariantsAsync);
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ShaderLanguageClient.ReadyChanged -= OnStateChanged;
            attached = false;
        }
        private void OnStateChanged(object sender, EventArgs e)
        {
            UpdateStatus();
            if (ShaderLanguageClient.Instance?.IsReady == true &&
                !string.Equals(catalogRoot, ShaderLanguageClient.Instance.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                catalogPath = null;
                Run(ReloadVariantsAsync);
            }
        }
        private void UpdateStatus()
        {
            status.Text = ShaderLanguageClient.Instance?.Status ?? "Open a GLSL or HLSL shader to start. F12 jumps to definitions; Ctrl+Space opens completions.";
            var variant = activated;
            active.Text = variant == null ? "Automatic shader context" : "Active: " + variant.Name + " · " + variant.EntryPoint + " · " + (variant.Stage ?? "auto");
        }

        private async Task ReloadVariantsAsync()
        {
            long version = Interlocked.Increment(ref refreshVersion);
            string root = await WorkspaceContext.GetRootAsync();
            if (string.IsNullOrEmpty(root)) { notice.Text = "Open a solution, folder or shader to load its variants."; return; }
            string path = catalogPath ?? Path.Combine(root, ".shader-variants.json");
            var entries = await Task.Run(() => VariantCatalog.Load(path, root), lifetime.Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(lifetime.Token);
            if (version != refreshVersion) return;
            catalogRoot = root;
            variants.Items.Clear();
            foreach (var group in entries.GroupBy(v => v.FilePath))
            {
                var file = new TreeViewItem { Header = Path.GetFileName(group.Key), ToolTip = group.Key, IsExpanded = true };
                foreach (var entry in group) file.Items.Add(new TreeViewItem { Header = entry.Name + "  ·  " + (entry.Stage ?? "auto"), Tag = entry, ToolTip = entry.EntryPoint });
                variants.Items.Add(file);
            }
            if (entries.Count == 0) notice.Text = "No variants yet. Use Edit… to define an entry point, stage, includes and macros. Ordinary shaders work with automatic context.";
            if (activated != null)
            {
                var updated = entries.FirstOrDefault(v => v.FilePath.Equals(activated.FilePath, StringComparison.OrdinalIgnoreCase) && v.Name == activated.Name);
                if (updated != null) await ActivateAsync(updated);
                else await ClearVariantAsync();
            }
        }

        private async Task LoadCatalogAsync()
        {
            var dialog = new OpenFileDialog { Title = "Load shader variants", Filter = "Shader variants (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            catalogPath = dialog.FileName;
            await ReloadVariantsAsync();
        }

        private Task ActivateSelectedAsync() => selected == null ? Task.CompletedTask : ActivateAsync(selected);
        private async Task ActivateAsync(ShaderVariant variant)
        {
            var client = ShaderCommands.Client();
            await client.SetVariantAsync(variant, lifetime.Token);
            UpdateStatus();
        }
        private async Task ClearVariantAsync()
        {
            if (ShaderLanguageClient.Instance != null)
                await ShaderLanguageClient.Instance.SetVariantAsync(null, lifetime.Token);
            UpdateStatus();
        }

        private async Task LoadDocumentSymbolsAsync()
        {
            string file = await WorkspaceContext.GetActiveFileAsync();
            if (ShaderContentTypes.ForPath(file) == null) throw new InvalidOperationException("Select a shader editor first.");
            var client = ShaderCommands.Client();
            await client.EnsureDocumentAsync(file, lifetime.Token);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await client.RequestAsync("textDocument/documentSymbol", new JObject { ["textDocument"] = new JObject { ["uri"] = new Uri(file).AbsoluteUri } }, deadline.Token);
                symbols.Items.Clear();
                foreach (var item in result as JArray ?? new JArray()) symbols.Items.Add(SymbolItem(item, new Uri(file).AbsoluteUri));
            }
        }
        private async Task LoadWorkspaceSymbolsAsync()
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var result = await ShaderCommands.Client().RequestAsync("workspace/symbol", new JObject { ["query"] = search.Text }, deadline.Token);
                symbols.Items.Clear();
                foreach (var item in result as JArray ?? new JArray()) symbols.Items.Add(SymbolItem(item, (string)item.SelectToken("location.uri")));
                if (symbols.Items.Count == 0) notice.Text = "No matching symbols. The server searches files it has indexed; open shader entry points to include their dependencies.";
            }
        }
        private TreeViewItem SymbolItem(JToken symbol, string fallbackUri)
        {
            var location = symbol["location"] as JObject ?? new JObject { ["uri"] = fallbackUri, ["range"] = symbol["selectionRange"] ?? symbol["range"] };
            var item = new TreeViewItem { Header = (string)symbol["name"], ToolTip = (string)symbol["detail"] ?? (string)symbol["containerName"], Tag = location, IsExpanded = true };
            foreach (var child in symbol["children"] as JArray ?? new JArray()) item.Items.Add(SymbolItem(child, fallbackUri));
            return item;
        }
        private Task NavigateSymbolAsync()
        {
            var location = (symbols.SelectedItem as TreeViewItem)?.Tag as JObject;
            if (location == null || !Uri.TryCreate((string)location["uri"], UriKind.Absolute, out Uri uri) || !uri.IsFile) return Task.CompletedTask;
            return WorkspaceContext.OpenFileAsync(uri.LocalPath, (int?)location.SelectToken("range.start.line") ?? 0, (int?)location.SelectToken("range.start.character") ?? 0);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            ShaderLanguageClient.ReadyChanged -= OnStateChanged;
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
        }
    }
}
