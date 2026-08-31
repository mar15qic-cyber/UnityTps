using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Batch EditMode test runner for bridge-driven validation. exec_editor_script hosts cannot
    /// reference UnityEditor.TestRunner directly, so this static facade runs inside the project
    /// editor assembly and exposes progress/result through static properties.
    /// </summary>
    public static class EditModeTestRunner
    {
        public static bool Running { get; private set; }
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>Runs all EditMode tests whose full name contains the given filter (null/empty = all EditMode tests).</summary>
        public static void Run(string testNameContains)
        {
            if (Running) { Debug.LogWarning("EditModeTestRunner already running"); return; }
            Running = true;
            LastSummary = string.Empty;

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var filter = new Filter { testMode = TestMode.EditMode };
            if (!string.IsNullOrWhiteSpace(testNameContains))
                filter.testNames = new[] { testNameContains };
            api.RegisterCallbacks(new Callbacks(summary =>
            {
                LastSummary = summary;
                Running = false;
                ScriptableObject.DestroyImmediate(api);
            }));
            api.Execute(new ExecutionSettings(filter));
        }

        private sealed class Callbacks : ICallbacks
        {
            private readonly System.Action<string> onFinished;
            public Callbacks(System.Action<string> onFinished) => this.onFinished = onFinished;

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"RESULT passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount} total={result.PassCount + result.FailCount + result.SkipCount}");
                CollectFailures(result, sb);
                onFinished(sb.ToString());
            }

            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            private static void CollectFailures(ITestResultAdaptor result, StringBuilder sb)
            {
                if (result == null) return;
                if (result.HasChildren == false && result.TestStatus == TestStatus.Failed)
                    sb.AppendLine($"FAILED {result.FullName}: {result.Message}");
                if (result.HasChildren)
                    foreach (var child in result.Children)
                        CollectFailures(child, sb);
            }
        }
    }
}
