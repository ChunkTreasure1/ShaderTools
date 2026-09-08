using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ShaderTools.VisualStudio.Editor
{
    public static class ShaderDiagnostics
    {
        private const int DiagnosticTagUnnecessary = 1;
        private const int DiagnosticSeverityError = 1;
        private const int MaximumDiagnostics = 8192;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, DiagnosticSubscription> Subscriptions =
            new Dictionary<string, DiagnosticSubscription>(StringComparer.OrdinalIgnoreCase);

        public static void Update(JToken parameters)
        {
            if (!(parameters is JObject parameterObject))
            {
                return;
            }

            var uri = NormalizeUri(parameterObject.Value<string>("uri"));
            if (uri == null)
            {
                return;
            }

            var diagnosticSet = ParseDiagnostics(parameterObject["diagnostics"] as JArray);
            ShaderInactiveCodeTagger[] subscribers;

            lock (Gate)
            {
                if (!Subscriptions.TryGetValue(uri, out var subscription))
                {
                    // Diagnostics for closed documents are intentionally not retained.
                    return;
                }

                subscription.Current = diagnosticSet;
                subscribers = subscription.GetLiveSubscribers();
                if (subscribers.Length == 0)
                {
                    Subscriptions.Remove(uri);
                    return;
                }
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.SetDiagnostics(uri, diagnosticSet);
            }
        }

        public static void Clear()
        {
            ShaderInactiveCodeTagger[] subscribers;
            lock (Gate)
            {
                subscribers = Subscriptions.Values
                    .SelectMany(subscription => subscription.GetLiveSubscribers())
                    .Distinct()
                    .ToArray();
                foreach (var subscription in Subscriptions.Values)
                {
                    subscription.Current = DiagnosticSet.Empty;
                }
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.ClearDiagnostics();
            }
        }

        internal static string NormalizeUri(string uriText)
        {
            if (string.IsNullOrWhiteSpace(uriText) ||
                !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            {
                return null;
            }

            return uri.AbsoluteUri;
        }

        internal static void Subscribe(string uri, ShaderInactiveCodeTagger tagger)
        {
            if (uri == null || tagger == null)
            {
                return;
            }

            DiagnosticSet current;
            lock (Gate)
            {
                if (!Subscriptions.TryGetValue(uri, out var subscription))
                {
                    subscription = new DiagnosticSubscription();
                    Subscriptions.Add(uri, subscription);
                }

                subscription.RemoveDeadOr(tagger);
                subscription.Subscribers.Add(new WeakReference<ShaderInactiveCodeTagger>(tagger));
                current = subscription.Current;
            }

            tagger.SetDiagnostics(uri, current);
        }

        internal static void Unsubscribe(string uri, ShaderInactiveCodeTagger tagger)
        {
            if (uri == null || tagger == null)
            {
                return;
            }

            lock (Gate)
            {
                if (!Subscriptions.TryGetValue(uri, out var subscription))
                {
                    return;
                }

                subscription.RemoveDeadOr(tagger);
                if (subscription.Subscribers.Count == 0)
                {
                    Subscriptions.Remove(uri);
                }
            }
        }

        internal static void Rebind(
            string oldUri,
            string newUri,
            ShaderInactiveCodeTagger tagger)
        {
            if (tagger == null || string.Equals(oldUri, newUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DiagnosticSet current = DiagnosticSet.Empty;
            lock (Gate)
            {
                if (oldUri != null && Subscriptions.TryGetValue(oldUri, out var oldSubscription))
                {
                    oldSubscription.RemoveDeadOr(tagger);
                    if (oldSubscription.Subscribers.Count == 0)
                    {
                        Subscriptions.Remove(oldUri);
                    }
                }

                if (newUri != null)
                {
                    if (!Subscriptions.TryGetValue(newUri, out var newSubscription))
                    {
                        newSubscription = new DiagnosticSubscription();
                        Subscriptions.Add(newUri, newSubscription);
                    }

                    newSubscription.RemoveDeadOr(tagger);
                    newSubscription.Subscribers.Add(new WeakReference<ShaderInactiveCodeTagger>(tagger));
                    current = newSubscription.Current;
                }

                // Change the tagger URI before releasing the subscription lock. An update
                // can therefore observe either the complete old binding or complete new one.
                tagger.ChangeDocumentUri(newUri, current);
            }
        }

        private static DiagnosticSet ParseDiagnostics(JArray diagnostics)
        {
            if (diagnostics == null || diagnostics.Count == 0)
            {
                return DiagnosticSet.Empty;
            }

            var inactiveRanges = new List<LspTextRange>();
            var errorRanges = new List<LspTextRange>();
            foreach (var token in diagnostics.Take(MaximumDiagnostics))
            {
                if (!(token is JObject diagnostic) ||
                    !LspTextRange.TryParse(diagnostic["range"], out var range))
                {
                    continue;
                }

                var severity = ReadInteger(diagnostic["severity"]);
                if (severity == DiagnosticSeverityError)
                {
                    errorRanges.Add(range);
                    continue;
                }

                if (HasUnnecessaryTag(diagnostic["tags"] as JArray))
                {
                    inactiveRanges.Add(range);
                }
            }

            return inactiveRanges.Count == 0 && errorRanges.Count == 0
                ? DiagnosticSet.Empty
                : new DiagnosticSet(inactiveRanges, errorRanges);
        }

        private static bool HasUnnecessaryTag(JArray tags)
        {
            if (tags == null)
            {
                return false;
            }

            foreach (var tag in tags)
            {
                if (ReadInteger(tag) == DiagnosticTagUnnecessary)
                {
                    return true;
                }
            }

            return false;
        }

        private static int? ReadInteger(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
            {
                return null;
            }

            var value = token.Value<long>();
            return value >= int.MinValue && value <= int.MaxValue ? (int?)value : null;
        }

        private sealed class DiagnosticSubscription
        {
            internal List<WeakReference<ShaderInactiveCodeTagger>> Subscribers { get; } =
                new List<WeakReference<ShaderInactiveCodeTagger>>();

            internal DiagnosticSet Current { get; set; } = DiagnosticSet.Empty;

            internal ShaderInactiveCodeTagger[] GetLiveSubscribers()
            {
                var result = new List<ShaderInactiveCodeTagger>(Subscribers.Count);
                for (var index = Subscribers.Count - 1; index >= 0; index--)
                {
                    if (Subscribers[index].TryGetTarget(out var target))
                    {
                        result.Add(target);
                    }
                    else
                    {
                        Subscribers.RemoveAt(index);
                    }
                }

                return result.ToArray();
            }

            internal void RemoveDeadOr(ShaderInactiveCodeTagger tagger)
            {
                for (var index = Subscribers.Count - 1; index >= 0; index--)
                {
                    if (!Subscribers[index].TryGetTarget(out var target) || ReferenceEquals(target, tagger))
                    {
                        Subscribers.RemoveAt(index);
                    }
                }
            }
        }
    }

    internal sealed class DiagnosticSet
    {
        internal static readonly DiagnosticSet Empty =
            new DiagnosticSet(Array.Empty<LspTextRange>(), Array.Empty<LspTextRange>());

        internal DiagnosticSet(
            IReadOnlyList<LspTextRange> inactiveRanges,
            IReadOnlyList<LspTextRange> errorRanges)
        {
            InactiveRanges = inactiveRanges;
            ErrorRanges = errorRanges;
        }

        internal IReadOnlyList<LspTextRange> InactiveRanges { get; }

        internal IReadOnlyList<LspTextRange> ErrorRanges { get; }
    }
}
