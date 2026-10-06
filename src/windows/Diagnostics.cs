using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaPen = System.Windows.Media.Pen;
using MediaPoint = System.Windows.Point;

namespace CodexHalo
{
public static class Diagnostics
    {
        public static int WriteCodexUsageSnapshot(string outputPath)
        {
            try
            {
                CodexUsageMonitor monitor = CodexUsageMonitor.Instance;
                monitor.RequestRefreshForTest();
                DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                while (monitor.IsRefreshing && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
                UsageMetrics metrics;
                monitor.TryRead(out metrics);
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["status"] = monitor.Status.ToString();
                result["has_five_hour"] = metrics != null && metrics.HasFiveHour;
                result["has_weekly"] = metrics != null && metrics.HasWeekly;
                result["has_context"] = metrics != null && metrics.HasContext;
                if (metrics != null && metrics.HasFiveHour)
                {
                    result["five_hour_used_percent"] = metrics.FiveHourUsedPercent;
                    result["five_hour_resets_at"] = Iso(metrics.FiveHourResetUtc);
                }
                if (metrics != null && metrics.HasWeekly)
                {
                    result["weekly_used_percent"] = metrics.WeeklyUsedPercent;
                    result["weekly_resets_at"] = Iso(metrics.WeeklyResetUtc);
                }
                if (metrics != null && metrics.HasContext)
                {
                    result["context_used_percent"] = metrics.ContextUsedPercent;
                }
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(outputPath, serializer.Serialize(result),
                    new UTF8Encoding(false));
                return monitor.Status == CodexUsageDataStatus.Fresh ? 0 : 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "{\"status\":\"Error\",\"detail\":\"" +
                    EscapeJson(ex.GetType().Name) + "\"}", new UTF8Encoding(false));
                return 1;
            }
        }

        private static string Iso(DateTime value)
        {
            return value == DateTime.MinValue ? String.Empty :
                value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static int RunSelfTest(string outputPath)
        {
            string diagnosticAppDirectory = Path.Combine(Path.GetTempPath(),
                "AgentHalo-self-test-app-" + Guid.NewGuid().ToString("N"));
            string previousDiagnosticDirectory =
                Environment.GetEnvironmentVariable(
                    "AGENTHALO_DIAGNOSTIC_APP_DIRECTORY");
            string previousTestMode = Environment.GetEnvironmentVariable(
                "AGENTHALO_TEST_MODE");
            Environment.SetEnvironmentVariable("AGENTHALO_TEST_MODE", "1");
            Environment.SetEnvironmentVariable(
                "AGENTHALO_DIAGNOSTIC_APP_DIRECTORY",
                diagnosticAppDirectory);
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "codex-halo-selftest-" +
                    Guid.NewGuid().ToString("N") + ".jsonl");
                string id = Guid.NewGuid().ToString();
                string now = DateTime.UtcNow.ToString("o");
                List<string> lines = new List<string>();
                lines.Add("{\"timestamp\":\"" + now + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" +
                    id + "\",\"cwd\":\"C:\\\\work\\\\halo\",\"model_provider\":\"ccswitch\"}}");
                lines.Add("{\"timestamp\":\"" + now +
                    "\",\"type\":\"turn_context\",\"payload\":{\"cwd\":\"C:\\\\work\\\\halo\"," +
                    "\"model\":\"glm-5.2\",\"collaboration_mode\":{\"mode\":\"default\"}}}");
                lines.Add("{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}");
                lines.Add("{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"shell_command\"}}");
                lines.Add("{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{" +
                    "\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":80}," +
                    "\"last_token_usage\":{\"input_tokens\":200,\"cached_input_tokens\":100,\"output_tokens\":20}," +
                    "\"model_context_window\":200000}}}");
                File.WriteAllLines(temp, lines.ToArray(), Encoding.UTF8);
                SessionTracker tracker = new SessionTracker(temp);
                Assert(tracker.Snapshot.ProjectName == "halo", "project metadata");
                Assert(tracker.Snapshot.State == HaloState.Working, "function call -> working");
                Assert(tracker.Snapshot.ModelName == "glm-5.2" &&
                    tracker.Snapshot.ModelProvider == "ccswitch",
                    "Codex model and provider metadata");
                Assert(tracker.Snapshot.TurnInputTokens == 200 &&
                    tracker.Snapshot.TurnCachedInputTokens == 100 &&
                    tracker.Snapshot.TurnOutputTokens == 20,
                    "Codex first turn token sample");
                Assert(tracker.Snapshot.ContextInputTokens == 200 &&
                    tracker.Snapshot.ContextWindowTokens == 200000,
                    "Codex context token sample");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{" +
                    "\"total_token_usage\":{\"input_tokens\":1600,\"cached_input_tokens\":700,\"output_tokens\":120}," +
                    "\"last_token_usage\":{\"input_tokens\":600,\"cached_input_tokens\":300,\"output_tokens\":40}," +
                    "\"model_context_window\":200000}}}\n", Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.TurnInputTokens == 800 &&
                    tracker.Snapshot.TurnCachedInputTokens == 400 &&
                    tracker.Snapshot.TurnOutputTokens == 60,
                    "Codex turn tokens use cumulative delta");

                CodexProviderProfile customProfile = new CodexProviderProfile
                {
                    Model = "glm-5.2",
                    ProviderId = "ccswitch",
                    ProviderName = "Custom Provider",
                    BaseUrl = "http://127.0.0.1:8317/v1",
                    IsCustomApi = true
                };
                CodexCustomApiMetrics customMetrics = CodexCustomApiMetricsReader.Read(
                    new List<SessionSnapshot> { tracker.Snapshot }, customProfile,
                    CodexUsageDataStatus.Fresh);
                Assert(customMetrics.IsCustomApi && customMetrics.Model == "glm-5.2" &&
                    customMetrics.ProjectName == "halo" &&
                    customMetrics.InputTokens == 800 && customMetrics.OutputTokens == 60,
                    "Codex custom API metrics use session metadata");
                CodexProviderProfile officialProfile = new CodexProviderProfile
                {
                    ProviderId = "openai",
                    BaseUrl = "https://api.openai.com/v1"
                };
                SessionSnapshot officialSnapshot = new SessionSnapshot
                {
                    Agent = AgentKind.Codex,
                    ModelProvider = "openai",
                    ModelName = "gpt-5.6-sol"
                };
                Assert(!CodexCustomApiMetricsReader.Read(
                    new List<SessionSnapshot> { officialSnapshot }, officialProfile,
                    CodexUsageDataStatus.Fresh).IsCustomApi,
                    "official OAuth remains quota mode");
                Assert(CodexCustomApiMetricsReader.Read(
                    new List<SessionSnapshot> { officialSnapshot }, officialProfile,
                    CodexUsageDataStatus.ApiKey).IsCustomApi,
                    "API key auth uses custom API panel");

                string configTemp = Path.Combine(Path.GetTempPath(),
                    "agent-halo-codex-config-" + Guid.NewGuid().ToString("N") + ".toml");
                File.WriteAllText(configTemp,
                    "model = \"deepseek-v4\"\nmodel_provider = \"ccswitch\"\n" +
                    "[model_providers.ccswitch]\nname = \"Private API\"\n" +
                    "base_url = \"http://127.0.0.1:8317/v1\"\n",
                    new UTF8Encoding(false));
                CodexProviderProfile parsedProfile = CodexProviderProfileReader.Read(configTemp);
                File.Delete(configTemp);
                Assert(parsedProfile.IsCustomApi && parsedProfile.Model == "deepseek-v4" &&
                    parsedProfile.ProviderName == "Private API",
                    "Codex custom provider config detection");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working,
                    "reasoning cannot override in-flight tool");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking,
                    "tool output returns business state to thinking immediately");
                Assert(tracker.Snapshot.TurnPhase == AgentTurnPhase.Thinking &&
                    tracker.Snapshot.Activity == AgentActivityKind.ReviewingResult,
                    "tool output records reviewing-result business dimensions");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\"," +
                    "\"call_id\":\"tool-a\",\"name\":\"shell_command\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\"," +
                    "\"call_id\":\"tool-b\",\"name\":\"apply_patch\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\"," +
                    "\"call_id\":\"tool-a\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working,
                    "one completed parallel tool does not close another active tool");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working,
                    "reasoning does not override a different active tool id");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\"," +
                    "\"call_id\":\"tool-b\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking,
                    "last parallel tool completion returns to thinking");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"reasoning_start\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking,
                    "generic reasoning_start is not misclassified as tool execution");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"tool_failed\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking &&
                    tracker.Snapshot.FailureSeverity ==
                        AgentFailureSeverity.RecoverableTool,
                    "recoverable tool failure does not become fatal error");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\"," +
                    "\"name\":\"apply_patch\",\"status\":\"completed\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working,
                    "custom tool call -> working");
                Assert(tracker.Snapshot.Action == "Editing files",
                    "apply_patch shows editing action");

                CodexRealtimeActivityReader realtime =
                    new CodexRealtimeActivityReader();
                string realtimeAdded =
                    "SSE event: {\"type\":\"response.output_item.added\",\"item\":{" +
                    "\"id\":\"ctc-test\",\"type\":\"custom_tool_call\"," +
                    "\"status\":\"in_progress\",\"name\":\"apply_patch\"}}";
                string realtimeDone =
                    "SSE event: {\"type\":\"response.output_item.done\",\"item\":{" +
                    "\"id\":\"ctc-test\",\"type\":\"custom_tool_call\"," +
                    "\"status\":\"completed\",\"name\":\"apply_patch\"}}";
                HaloState realtimeState;
                string realtimeAction;
                Assert(realtime.FindActive(new[] { realtimeAdded },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Working &&
                    realtimeAction == "Editing files",
                    "live apply_patch start -> working");
                Assert(!realtime.FindActive(new[] { realtimeDone, realtimeAdded },
                    out realtimeState, out realtimeAction),
                    "live apply_patch done clears realtime working");
                string realtimeMessageAdded =
                    "SSE event: {\"type\":\"response.output_item.added\",\"item\":{" +
                    "\"id\":\"msg-test\",\"type\":\"message\",\"status\":\"in_progress\"}}";
                string realtimeMessageDone =
                    "SSE event: {\"type\":\"response.output_item.done\",\"item\":{" +
                    "\"id\":\"msg-test\",\"type\":\"message\",\"status\":\"completed\"}}";
                Assert(realtime.FindActive(new[] { realtimeMessageAdded },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Working &&
                    realtimeAction == "Generating response",
                    "live unphased message -> generic working response");
                Assert(!realtime.FindActive(new[] { realtimeMessageDone, realtimeMessageAdded },
                    out realtimeState, out realtimeAction),
                    "live final answer done clears realtime working");
                string realtimeTextDelta =
                    "SSE event: {\"type\":\"response.output_text.delta\"," +
                    "\"delta\":\"hello\"}";
                string realtimeTextDone =
                    "SSE event: {\"type\":\"response.output_text.done\"}";
                string realtimeCompleted =
                    "SSE event: {\"type\":\"response.completed\",\"response\":{" +
                    "\"id\":\"resp-test\"}}";
                Assert(realtime.FindActive(new[] { realtimeTextDelta },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Working &&
                    realtimeAction == "Generating response",
                    "live text delta -> working");
                string realtimeContextCompactDelta =
                    "SSE event: {\"type\":\"response.output_text.delta\"," +
                    "\"delta\":\"Compressing context\"}";
                Assert(realtime.FindActive(new[] { realtimeContextCompactDelta },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Working &&
                    realtimeAction == "Compressing context",
                    "live context compact delta -> working");
                Assert(!realtime.FindActive(new[] { realtimeCompleted, realtimeTextDelta },
                    out realtimeState, out realtimeAction),
                    "live response completed clears realtime working");
                Assert(!realtime.FindActive(new[] { realtimeTextDone, realtimeTextDelta },
                    out realtimeState, out realtimeAction),
                    "live text done clears realtime working");
                string realtimeInputAdded =
                    "SSE event: {\"type\":\"response.output_item.added\",\"item\":{" +
                    "\"id\":\"input-test\",\"type\":\"function_call\"," +
                    "\"status\":\"in_progress\",\"name\":\"request_user_input\"}}";
                Assert(realtime.FindActive(new[] { realtimeInputAdded },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Attention,
                    "live request_user_input -> attention");
                string realtimeArgumentsDelta =
                    "SSE event: {\"type\":\"response.function_call_arguments.delta\"," +
                    "\"item_id\":\"fc-test\",\"delta\":\"{\\\"cmd\\\":\\\"git\"}";
                string realtimeArgumentsDone =
                    "SSE event: {\"type\":\"response.function_call_arguments.done\"," +
                    "\"item_id\":\"fc-test\"}";
                string realtimeFunctionDone =
                    "SSE event: {\"type\":\"response.output_item.done\",\"item\":{" +
                    "\"id\":\"fc-test\",\"type\":\"function_call\"," +
                    "\"status\":\"completed\",\"name\":\"exec_command\"}}";
                Assert(realtime.FindActive(new[] { realtimeArgumentsDelta },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Working,
                    "live function argument stream keeps Codex active");
                Assert(!realtime.FindActive(new[] { realtimeFunctionDone,
                    realtimeArgumentsDone, realtimeArgumentsDelta },
                    out realtimeState, out realtimeAction),
                    "live function argument stream clears after item done");
                string realtimeEscalatedArguments =
                    "SSE event: {\"type\":\"response.function_call_arguments.delta\"," +
                    "\"item_id\":\"fc-approval\",\"delta\":\"require_escalated sandbox_permissions justification\"}";
                Assert(realtime.FindActive(new[] { realtimeEscalatedArguments },
                    out realtimeState, out realtimeAction) &&
                    realtimeState == HaloState.Attention,
                    "live escalated command arguments -> attention");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking,
                    "completed custom tool returns business state to thinking");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\"," +
                    "\"name\":\"request_user_input\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention &&
                    tracker.Snapshot.AttentionReason == AgentAttentionReason.UserInput,
                    "request_user_input -> attention");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\"," +
                    "\"name\":\"exec_command\",\"arguments\":\"{" +
                    "\\\"sandbox_permissions\\\":\\\"require_escalated\\\"," +
                    "\\\"justification\\\":\\\"approve\\\"}\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention &&
                    tracker.Snapshot.AttentionReason ==
                        AgentAttentionReason.CommandConfirmation,
                    "escalated exec command -> attention, got " +
                    tracker.Snapshot.State + " / " + tracker.Snapshot.AttentionReason +
                    " / " + tracker.Snapshot.Action);

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"approval_requested\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention &&
                    tracker.Snapshot.AttentionReason == AgentAttentionReason.Approval,
                    "approval request -> attention");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_failed\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Error &&
                    tracker.Snapshot.FailureSeverity == AgentFailureSeverity.FatalTurn,
                    "terminal turn failure -> error");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\"," +
                    "\"role\":\"assistant\",\"phase\":\"final_answer\"," +
                    "\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working &&
                    tracker.Snapshot.Action == "Writing answer" &&
                    tracker.Snapshot.TurnPhase == AgentTurnPhase.Answering,
                    "normal final answer outputs as working");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"," +
                    "\"phase\":\"final_answer\",\"message\":\"done\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working &&
                    tracker.Snapshot.Action == "Writing answer",
                    "final answer agent message outputs as working");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Done, "task complete -> done");
                Assert(!tracker.Snapshot.Active, "task complete deactivates session");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Thinking,
                    "plan task starts thinking");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\"," +
                    "\"role\":\"assistant\",\"phase\":\"final_answer\"," +
                    "\"content\":[{\"type\":\"output_text\",\"text\":\"plain answer\"}]}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working,
                    "plain plan final answer outputs as working, got " +
                    tracker.Snapshot.State + " / " + tracker.Snapshot.Action);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Done &&
                    !tracker.Snapshot.Active,
                    "plain plan complete becomes done");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\"," +
                    "\"role\":\"assistant\",\"phase\":\"final_answer\"," +
                    "\"content\":[{\"type\":\"output_text\",\"text\":\"<proposed_plan>\"}]}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Working &&
                    tracker.Snapshot.Action == "Writing answer",
                    "plan final answer outputs as working");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention &&
                    tracker.Snapshot.Active,
                    "plan complete waits for user choice");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"turn_context\",\"payload\":{\"collaboration_mode\":{" +
                    "\"mode\":\"plan\"}}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\"," +
                    "\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\"," +
                    "\"text\":\"<proposed_plan>\"}]}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention,
                    "turn_context plan task complete -> attention");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Done,
                    "plan without final answer -> done");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"," +
                    "\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\"," +
                    "\"text\":\"<proposed_plan>\"}]}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention,
                    "plan round 1 attention");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Done,
                    "plan flag resets across turns");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\"," +
                    "\"item\":{\"type\":\"Plan\",\"text\":\"Plan body\"}}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Attention,
                    "completed plan item waits for user choice");

                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"," +
                    "\"collaboration_mode_kind\":\"plan\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_failed\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Error,
                    "plan fatal turn -> error");
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                    Encoding.UTF8);
                File.AppendAllText(temp, "{\"timestamp\":\"" + now +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                    Encoding.UTF8);
                tracker.Refresh();
                Assert(tracker.Snapshot.State == HaloState.Done,
                    "fatal turn clears plan flag");
                Assert(GeneratedHaloSpec.ContractVersion == 2,
                    "generated shared contract version");
                Assert(GeneratedHaloSpec.ReleaseVersion == "0.16.0",
                    "generated shared release version");
                Assert(GeneratedHaloSpec.State(HaloState.Attention).Label == "NEEDS YOU",
                    "generated state labels");
                Assert(GeneratedHaloSpec.FriendlyAction("apply_patch") == "Editing files",
                    "generated action rules");
                Assert(GeneratedHaloSpec.ClassifyFailure("server overloaded") ==
                    "failure.service_unavailable", "generated failure rules");
                L10n.Instance.SetLanguage("zh");
                HaloWindow.ConfigureLocalization(new HaloSettings { Language = "en" });
                Assert(L10n.Instance.CurrentLanguage == "en",
                    "saved Windows language initializes L10n before UI text is built");
                Assert(HaloWindow.IsLanguageMenuItemChecked(null, null),
                    "auto language item is checked when preference follows system");
                Assert(!HaloWindow.IsLanguageMenuItemChecked("en", null),
                    "resolved system language does not check explicit English item");
                Assert(HaloWindow.IsLanguageMenuItemChecked("en", "en"),
                    "explicit English language item is checked when preference is English");
                L10n.Instance.SetLanguage("zh");
                Assert(Math.Abs(HaloVisual.DiagnosticGapSeparation(0) - 40) < 0.001,
                    "magnetic repulsion starts at minimum separation");
                Assert(Math.Abs(HaloVisual.DiagnosticGapSeparation(1) - 150) < 0.001,
                    "magnetic repulsion ends at maximum separation");
                Assert(HaloVisual.DiagnosticRepulsionDuration(28) >
                    HaloVisual.DiagnosticRepulsionDuration(80),
                    "slow orbit uses slower magnetic repulsion");
                Assert(HaloVisual.DiagnosticBreath(HaloState.Thinking, 1.0) >
                    HaloVisual.DiagnosticBreath(HaloState.Thinking, 4.6),
                    "thinking uses long bright and short dim cadence");
                Assert(HaloVisual.DiagnosticBreath(HaloState.Done, 2.0) >
                    HaloVisual.DiagnosticBreath(HaloState.Done, 8.0),
                    "done uses long bright and short dim cadence");
                Assert(HaloVisual.DiagnosticPowered(HaloState.Thinking, 1.0) > 0.85,
                    "thinking has a bright sustained plateau");
                Assert(HaloVisual.DiagnosticPowered(HaloState.Working, 0.8) > 0.88,
                    "working uses a bright sustained plateau");
                Assert(HaloVisual.DiagnosticPowered(HaloState.Working, 6.35) < 0.35,
                    "working includes a shorter dim interval");
                Assert(HaloVisual.DiagnosticTransitionLight(0.9, 0.8, 0.48) < 0.12,
                    "state transition changes color while the ring is dim");
                Assert(HaloVisual.DiagnosticTransitionLight(0.9, 0.0, 0.99) < 0.01,
                    "steady green transition finishes without glow");
                Assert(HaloVisual.DiagnosticAttentionPulse(0.54) > 0.88,
                    "attention first pulse is clearly visible");
                Assert(HaloVisual.DiagnosticAttentionPulse(1.24) > 0.70,
                    "attention second pulse is visible and softer");
                Assert(HaloVisual.DiagnosticAttentionPulse(2.55) < 0.24,
                    "attention leaves a quiet living interval");
                Assert(HaloVisual.DiagnosticPowered(HaloState.Thinking, 0.8) > 0.97,
                    "thinking reaches the full bright tier");
                Assert(HaloVisual.DiagnosticPowered(HaloState.Working, 0.8) > 0.97,
                    "working reaches the full bright tier");
                Assert(HaloVisual.DiagnosticBrightDuration(HaloState.Thinking) <
                    HaloVisual.DiagnosticBrightDuration(HaloState.Working),
                    "thinking bright duration is shorter than working");
                Assert(HaloVisual.DiagnosticCoreWhite(HaloState.Thinking) >
                    HaloVisual.DiagnosticCoreWhite(HaloState.Done),
                    "yellow receives perceptual white-core compensation");
                Assert(Math.Abs(HaloWindow.DiagnosticSizeForScale(75) - 84) < 0.001,
                    "75 percent halo size");
                Assert(Math.Abs(HaloWindow.DiagnosticSizeForScale(100) - 112) < 0.001,
                    "100 percent halo size");
                Assert(Math.Abs(HaloWindow.DiagnosticSizeForScale(125) - 140) < 0.001,
                    "125 percent halo size");
                Assert(Math.Abs(HaloWindow.DiagnosticSizeForScale(150) - 112) < 0.001,
                    "removed 150 percent size falls back to 100 percent");
                Assert(Math.Abs(HaloWindow.DiagnosticSizeForScale(99) - 112) < 0.001,
                    "invalid halo size falls back to 100 percent");
                List<System.Drawing.Rectangle> displayAreas =
                    new List<System.Drawing.Rectangle>
                    {
                        new System.Drawing.Rectangle(0, 0, 1920, 1040),
                        new System.Drawing.Rectangle(1920, 0, 2560, 1400)
                    };
                Assert(HaloWindow.DiagnosticIsFrameVisible(
                    new System.Drawing.Rectangle(1800, 900, 112, 112), displayAreas),
                    "on-screen halo remains visible");
                Assert(HaloWindow.DiagnosticIsFrameVisible(
                    new System.Drawing.Rectangle(4440, 1300, 112, 112), displayAreas),
                    "partially visible halo remains visible");
                Assert(!HaloWindow.DiagnosticIsFrameVisible(
                    new System.Drawing.Rectangle(4600, 1500, 112, 112), displayAreas),
                    "off-screen halo requires recovery");
                DateTime topmostCheckUtc =
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Assert(TopmostGuardPolicy.CheckInterval == TimeSpan.FromSeconds(3),
                    "topmost guard uses a low-frequency three-second interval");
                Assert(TopmostGuardPolicy.IsDue(
                    topmostCheckUtc, DateTime.MinValue),
                    "topmost guard runs immediately before its first schedule");
                DateTime nextTopmostCheckUtc =
                    TopmostGuardPolicy.NextCheckUtc(topmostCheckUtc);
                Assert(!TopmostGuardPolicy.IsDue(
                    topmostCheckUtc.AddMilliseconds(2999), nextTopmostCheckUtc),
                    "topmost guard remains throttled before three seconds");
                Assert(TopmostGuardPolicy.IsDue(
                    topmostCheckUtc.AddSeconds(3), nextTopmostCheckUtc),
                    "topmost guard becomes due at three seconds");
                Assert(!TopmostGuardPolicy.ShouldRestore(
                    false, false, false, true, true),
                    "disabled always-on-top never restores");
                Assert(!TopmostGuardPolicy.ShouldRestore(
                    true, true, false, true, true),
                    "D3D full-screen activity suppresses restoration");
                Assert(TopmostGuardPolicy.ShouldRestore(
                    true, false, false, false, false),
                    "missing native topmost state triggers restoration");
                Assert(TopmostGuardPolicy.ShouldRestore(
                    true, false, true, true, false),
                    "foreground changes reassert the topmost order");
                Assert(!TopmostGuardPolicy.ShouldRestore(
                    true, false, true, false, false),
                    "stable native topmost state avoids redundant work");
                Assert(TopmostGuardPolicy.ShouldRestore(
                    true, false, true, false, true),
                    "forced lifecycle recovery reasserts topmost order");
                DateTime nextRuntimeStateRefreshUtc =
                    HaloWindow.NextRuntimeStateRefreshUtc(topmostCheckUtc);
                Assert(!HaloWindow.IsRuntimeStateRefreshDue(
                    topmostCheckUtc.AddMilliseconds(999),
                    nextRuntimeStateRefreshUtc),
                    "runtime-state refresh waits for its one-second interval");
                Assert(!HaloWindow.ShouldRefreshRuntimeState(
                    false, false, topmostCheckUtc.AddMilliseconds(999),
                    nextRuntimeStateRefreshUtc),
                    "unchanged foreground remains idle before runtime refresh is due");
                Assert(HaloWindow.IsRuntimeStateRefreshDue(
                    topmostCheckUtc.AddSeconds(1), nextRuntimeStateRefreshUtc),
                    "runtime-state refresh is due after one second");
                Assert(HaloWindow.ShouldRefreshRuntimeState(
                    false, false, topmostCheckUtc.AddSeconds(1),
                    nextRuntimeStateRefreshUtc),
                    "completed-state expiry and process exit refresh without a foreground change");
                Assert(CodexRuntimeReader.IsCodexDesktopProcessName("codex") &&
                    CodexRuntimeReader.IsCodexDesktopProcessName("Codex"),
                    "the Codex desktop executable is recognized case-insensitively");
                Assert(!CodexRuntimeReader.IsCodexDesktopProcessName(
                    "codex-code-mode-host") &&
                    !CodexRuntimeReader.IsCodexDesktopProcessName(
                    "codex-command-runner-0.145.0-alpha.30"),
                    "Codex helper processes do not keep the desktop presence online");
                MediaColor workingBlue = HaloVisual.StateColor(HaloState.Working);
                MediaColor completedGreen = HaloVisual.StateColor(HaloState.Done);
                Assert(ColorSaturation(workingBlue) >=
                    ColorSaturation(completedGreen) - 0.02,
                    "Windows execution blue matches completed green saturation");
                using (Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip())
                {
                    Forms.ToolStripMenuItem checkedItem =
                        new Forms.ToolStripMenuItem("始终置顶");
                    checkedItem.Checked = true;
                    menu.Items.Add(checkedItem);
                    Win11MenuRenderer.Apply(menu);
                    Assert(menu.Renderer is Win11MenuRenderer,
                        "Windows 11 menu renderer is applied");
                    Assert(checkedItem.Padding.Left == 8 &&
                        checkedItem.Padding.Top == 6,
                        "Windows 11 menu items use compact inset padding");
                }
                UsageMetrics usage = new UsageMetrics
                {
                    ContextInputTokens = 202600,
                    ContextWindowTokens = 258400
                };
                Assert(Math.Abs(usage.ContextUsedPercent - 78.405) < 0.01,
                    "context uses latest input tokens rather than cumulative usage");
                DateTime localReset = DateTime.Today.AddHours(14).AddMinutes(58);
                Assert(DetailsWindow.FormatResetTime(localReset.ToUniversalTime()) ==
                    "14:58 刷新", "same-day quota reset formatting");
                Assert(String.IsNullOrEmpty(DetailsWindow.FormatResetTime(
                    DateTime.MinValue)), "missing reset time stays hidden");
                Assert(DetailsWindow.IsQuotaExpired(
                    DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow),
                    "expired quota snapshot is stale");
                Assert(!DetailsWindow.IsQuotaExpired(
                    DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow),
                    "future quota reset remains valid");
                DateTime fiveHourReset = DateTime.UtcNow.AddHours(4);
                DateTime weeklyReset = DateTime.UtcNow.AddDays(4);
                QuotaDisplaySet quotaSet = QuotaDisplaySelector.Select(
                    new UsageMetrics
                    {
                        HasFiveHour = true,
                        FiveHourUsedPercent = 23,
                        FiveHourResetUtc = fiveHourReset,
                        HasWeekly = true,
                        WeeklyUsedPercent = 67,
                        WeeklyResetUtc = weeklyReset
                    });
                Assert(quotaSet.FiveHour.HasQuota &&
                    quotaSet.FiveHour.Kind == QuotaDisplayKind.FiveHour &&
                    quotaSet.FiveHour.LabelKey == "quota.5h" &&
                    Math.Abs(quotaSet.FiveHour.UsedPercent - 23) < 0.001 &&
                    quotaSet.FiveHour.ResetUtc == fiveHourReset &&
                    quotaSet.LongTerm.HasQuota &&
                    quotaSet.LongTerm.Kind == QuotaDisplayKind.Weekly &&
                    Math.Abs(quotaSet.LongTerm.UsedPercent - 67) < 0.001 &&
                    quotaSet.LongTerm.ResetUtc == weeklyReset,
                    "five-hour and weekly quotas stay independently visible");

                DateTime weeklyOnlyReset = DateTime.UtcNow.AddDays(5);
                quotaSet = QuotaDisplaySelector.Select(new UsageMetrics
                {
                    HasWeekly = true,
                    WeeklyUsedPercent = 42,
                    WeeklyResetUtc = weeklyOnlyReset
                });
                Assert(!quotaSet.FiveHour.HasQuota &&
                    quotaSet.FiveHour.Kind == QuotaDisplayKind.None &&
                    quotaSet.LongTerm.HasQuota &&
                    quotaSet.LongTerm.Kind == QuotaDisplayKind.Weekly &&
                    quotaSet.LongTerm.LabelKey == "quota.weekly" &&
                    Math.Abs(quotaSet.LongTerm.UsedPercent - 42) < 0.001 &&
                    quotaSet.LongTerm.ResetUtc == weeklyOnlyReset,
                    "weekly-only data clears five-hour and keeps weekly");

                DateTime currentAvailableReset = DateTime.UtcNow.AddDays(20);
                quotaSet = QuotaDisplaySelector.Select(new UsageMetrics
                {
                    HasMonthly = true,
                    MonthlyUsedPercent = 35,
                    MonthlyResetUtc = currentAvailableReset
                });
                Assert(!quotaSet.FiveHour.HasQuota &&
                    quotaSet.LongTerm.HasQuota &&
                    quotaSet.LongTerm.Kind == QuotaDisplayKind.CurrentAvailable &&
                    quotaSet.LongTerm.LabelKey == "quota.current_available" &&
                    Math.Abs(quotaSet.LongTerm.UsedPercent - 35) < 0.001 &&
                    quotaSet.LongTerm.ResetUtc == currentAvailableReset,
                    "monthly or credits data uses the generic available quota");

                fiveHourReset = DateTime.UtcNow.AddHours(3);
                quotaSet = QuotaDisplaySelector.Select(new UsageMetrics
                {
                    HasFiveHour = true,
                    FiveHourUsedPercent = 88,
                    FiveHourResetUtc = fiveHourReset
                });
                Assert(quotaSet.FiveHour.HasQuota &&
                    quotaSet.FiveHour.Kind == QuotaDisplayKind.FiveHour &&
                    Math.Abs(quotaSet.FiveHour.UsedPercent - 88) < 0.001 &&
                    quotaSet.FiveHour.ResetUtc == fiveHourReset &&
                    !quotaSet.LongTerm.HasQuota &&
                    quotaSet.LongTerm.Kind == QuotaDisplayKind.None &&
                    quotaSet.LongTerm.LabelKey == "quota.weekly" &&
                    quotaSet.LongTerm.ResetUtc == DateTime.MinValue,
                    "five-hour-only data remains five-hour without fabricating weekly");

                quotaSet = QuotaDisplaySelector.Select(new UsageMetrics());
                Assert(!quotaSet.FiveHour.HasQuota &&
                    quotaSet.FiveHour.Kind == QuotaDisplayKind.None &&
                    Math.Abs(quotaSet.FiveHour.UsedPercent) < 0.001 &&
                    quotaSet.FiveHour.ResetUtc == DateTime.MinValue &&
                    !quotaSet.LongTerm.HasQuota &&
                    quotaSet.LongTerm.Kind == QuotaDisplayKind.None &&
                    quotaSet.LongTerm.LabelKey == "quota.weekly" &&
                    Math.Abs(quotaSet.LongTerm.UsedPercent) < 0.001 &&
                    quotaSet.LongTerm.ResetUtc == DateTime.MinValue,
                    "missing quotas clear both percentages and reset times");
                string contextOnlyRate =
                    "{\"payload\":{\"info\":{\"rate_limits\":{}," +
                    "\"last_token_usage\":{\"input_tokens\":50}," +
                    "\"model_context_window\":100}}}";
                string quotaOnlyRate =
                    "{\"payload\":{\"info\":{\"rate_limits\":{\"primary\":{" +
                    "\"used_percent\":25,\"window_minutes\":300," +
                    "\"resets_at\":4102444800}," +
                    "\"secondary\":{\"used_percent\":40,\"window_minutes\":10080," +
                    "\"resets_at\":4102444800}}}}}";
                UsageMetrics parsedUsage;
                Assert(RateLimitReader.TryReadFromNewestLinesForTest(
                    new[] { contextOnlyRate, quotaOnlyRate }, out parsedUsage),
                    "rate limit parser reads split snapshots");
                Assert(parsedUsage.HasFiveHour && parsedUsage.HasWeekly &&
                    parsedUsage.HasContext, "rate limit parser fills all fields");
                Assert(Math.Abs(parsedUsage.FiveHourUsedPercent - 25) < 0.001 &&
                    Math.Abs(parsedUsage.WeeklyUsedPercent - 40) < 0.001 &&
                    Math.Abs(parsedUsage.ContextUsedPercent - 50) < 0.001,
                    "rate limit parser preserves latest field values");
                string weeklyOnlyRate =
                    "{\"payload\":{\"info\":{\"rate_limits\":{\"primary\":{" +
                    "\"used_percent\":0,\"window_minutes\":10080," +
                    "\"resets_at\":4102444800},\"secondary\":null}," +
                    "\"last_token_usage\":{\"input_tokens\":10}," +
                    "\"model_context_window\":100}}}";
                Assert(RateLimitReader.TryReadFromNewestLinesForTest(
                    new[] { weeklyOnlyRate }, out parsedUsage) &&
                    parsedUsage.HasWeekly && !parsedUsage.HasFiveHour &&
                    Math.Abs(parsedUsage.WeeklyUsedPercent) < 0.001,
                    "single primary 10080-minute window is weekly, not five-hour");
                string monthlyRate =
                    "{\"payload\":{\"info\":{\"rate_limits\":{\"monthly\":{" +
                    "\"used_percent\":37,\"resets_at\":4102444800}}," +
                    "\"last_token_usage\":{\"input_tokens\":25}," +
                    "\"model_context_window\":100}}}";
                Assert(RateLimitReader.TryReadFromNewestLinesForTest(
                    new[] { monthlyRate }, out parsedUsage),
                    "rate limit parser reads monthly quota");
                Assert(parsedUsage.HasMonthly && !parsedUsage.HasFiveHour &&
                    !parsedUsage.HasWeekly &&
                    Math.Abs(parsedUsage.MonthlyUsedPercent - 37) < 0.001,
                    "monthly quota stays separate from Plus buckets");
                string longPrimaryRate =
                    "{\"payload\":{\"info\":{\"rate_limits\":{\"primary\":{" +
                    "\"used_percent\":41,\"window_minutes\":43200," +
                    "\"resets_at\":4102444800}}}}}";
                Assert(RateLimitReader.TryReadFromNewestLinesForTest(
                    new[] { longPrimaryRate }, out parsedUsage) &&
                    parsedUsage.HasMonthly &&
                    Math.Abs(parsedUsage.MonthlyUsedPercent - 41) < 0.001,
                    "single long-window primary quota becomes monthly");

                string liveUsage = "{\"plan_type\":\"plus\",\"rate_limit\":{" +
                    "\"primary_window\":{\"used_percent\":24," +
                    "\"limit_window_seconds\":18000,\"reset_after_seconds\":900}," +
                    "\"secondary_window\":{\"used_percent\":61," +
                    "\"limit_window_seconds\":604800,\"reset_after_seconds\":7200}}}";
                Assert(CodexUsageResponseMapper.TryMapForTest(liveUsage,
                    DateTime.UtcNow, out parsedUsage) &&
                    parsedUsage.HasFiveHour && parsedUsage.HasWeekly,
                    "OAuth usage response maps both quota windows");
                Assert(Math.Abs(parsedUsage.FiveHourUsedPercent - 24) < 0.001 &&
                    Math.Abs(parsedUsage.WeeklyUsedPercent - 61) < 0.001,
                    "OAuth quota percentages retain their window identity");

                string weeklyPrimaryUsage = "{\"rate_limit\":{" +
                    "\"primary_window\":{\"used_percent\":17," +
                    "\"limit_window_seconds\":604800," +
                    "\"reset_after_seconds\":3600},\"secondary_window\":null}}";
                Assert(CodexUsageResponseMapper.TryMapForTest(weeklyPrimaryUsage,
                    DateTime.UtcNow, out parsedUsage) && parsedUsage.HasWeekly &&
                    !parsedUsage.HasFiveHour &&
                    Math.Abs(parsedUsage.WeeklyUsedPercent - 17) < 0.001,
                    "OAuth weekly primary is not misclassified as five-hour quota");

                DateTime mergeNow = DateTime.UtcNow;
                UsageMetrics localQuota = new UsageMetrics
                {
                    HasFiveHour = true,
                    FiveHourUsedPercent = 80,
                    FiveHourResetUtc = mergeNow.AddHours(2),
                    HasWeekly = true,
                    WeeklyUsedPercent = 70,
                    WeeklyResetUtc = mergeNow.AddDays(2),
                    ContextInputTokens = 50,
                    ContextWindowTokens = 100
                };
                UsageMetrics remoteQuota = new UsageMetrics
                {
                    HasFiveHour = true,
                    FiveHourUsedPercent = 20,
                    FiveHourResetUtc = mergeNow.AddHours(3),
                    HasWeekly = true,
                    WeeklyUsedPercent = 30,
                    WeeklyResetUtc = mergeNow.AddDays(3),
                    ContextInputTokens = -1
                };
                UsageMetrics mergedQuota = CodexUsageMonitor.MergeForTest(
                    localQuota, remoteQuota, mergeNow);
                Assert(Math.Abs(mergedQuota.FiveHourUsedPercent - 20) < 0.001 &&
                    Math.Abs(mergedQuota.WeeklyUsedPercent - 30) < 0.001 &&
                    Math.Abs(mergedQuota.ContextUsedPercent - 50) < 0.001,
                    "live OAuth quota overrides JSONL while JSONL supplies context");
                mergedQuota.FiveHourUsedPercent = 99;
                mergedQuota.ContextInputTokens = 0;
                UsageMetrics rereadQuota = CodexUsageMonitor.MergeForTest(
                    localQuota, remoteQuota, mergeNow);
                Assert(rereadQuota.FiveHourUsedPercent == 20 &&
                    rereadQuota.ContextInputTokens == 50 &&
                    localQuota.FiveHourUsedPercent == 80,
                    "usage merge returns an independent result");
                UsageMetrics fallbackQuota = CodexUsageMonitor.MergeForTest(
                    localQuota, null, mergeNow);
                Assert(Math.Abs(fallbackQuota.FiveHourUsedPercent - 80) < 0.001 &&
                    Math.Abs(fallbackQuota.WeeklyUsedPercent - 70) < 0.001,
                    "JSONL quota remains available when OAuth has no snapshot");
                remoteQuota.FiveHourResetUtc = mergeNow.AddMinutes(-1);
                UsageMetrics expiredRemoteQuota = CodexUsageMonitor.MergeForTest(
                    localQuota, remoteQuota, mergeNow);
                Assert(Math.Abs(expiredRemoteQuota.FiveHourUsedPercent - 80) < 0.001,
                    "expired OAuth window does not replace a current JSONL fallback");


                DateTime supersessionNow = DateTime.UtcNow;
                SessionSnapshot oldError = new SessionSnapshot
                {
                    ThreadId = "old-error",
                    ProjectName = "OldProject",
                    State = HaloState.Error,
                    Action = "Interrupted",
                    LastEventUtc = supersessionNow.AddMinutes(-1),
                    Active = false,
                    Agent = AgentKind.Codex
                };
                SessionSnapshot newerWorking = new SessionSnapshot
                {
                    ThreadId = "new-working",
                    ProjectName = "NewProject",
                    State = HaloState.Working,
                    Action = "Running command",
                    LastEventUtc = supersessionNow,
                    Active = true,
                    Agent = AgentKind.Codex
                };
                List<SessionSnapshot> supersessionInput =
                    new List<SessionSnapshot> { oldError, newerWorking };
                List<SessionSnapshot> supersessionDisplay =
                    CodexSessionMonitor.WithoutSupersededErrors(supersessionInput);
                Assert(supersessionDisplay.Count == 1 &&
                    supersessionDisplay[0].ThreadId == "new-working",
                    "newer Windows session removes old interrupted display state");
                Assert(supersessionInput.Count == 2,
                    "Windows supersession filter preserves raw sessions");

                SessionSnapshot newerDone = new SessionSnapshot
                {
                    ThreadId = "new-done",
                    ProjectName = "NewProject",
                    State = HaloState.Done,
                    Action = "Complete",
                    LastEventUtc = supersessionNow,
                    CompletedUtc = supersessionNow,
                    Active = false,
                    Agent = AgentKind.Codex
                };
                List<SessionSnapshot> doneDisplay =
                    CodexSessionMonitor.WithoutSupersededErrors(
                        new[] { oldError, newerDone });
                Assert(doneDisplay.Count == 1 &&
                    doneDisplay[0].ThreadId == "new-done",
                    "newer Windows completion removes old interrupted display state");
                List<SessionSnapshot> acknowledgedDoneDisplay = doneDisplay
                    .Where(delegate(SessionSnapshot snapshot)
                    {
                        return snapshot.State != HaloState.Done;
                    })
                    .ToList();
                Assert(acknowledgedDoneDisplay.Count == 0,
                    "acknowledged Windows completion does not resurrect old error");

                SessionSnapshot olderWorking = new SessionSnapshot
                {
                    ThreadId = "old-working",
                    ProjectName = "OldProject",
                    State = HaloState.Working,
                    Action = "Running command",
                    LastEventUtc = supersessionNow.AddMinutes(-1),
                    Active = true,
                    Agent = AgentKind.Codex
                };
                SessionSnapshot newerError = new SessionSnapshot
                {
                    ThreadId = "new-error",
                    ProjectName = "NewProject",
                    State = HaloState.Error,
                    Action = "Interrupted",
                    LastEventUtc = supersessionNow,
                    Active = false,
                    Agent = AgentKind.Codex
                };
                List<SessionSnapshot> latestErrorDisplay =
                    CodexSessionMonitor.WithoutSupersededErrors(
                        new[] { olderWorking, newerError });
                Assert(latestErrorDisplay.Count == 2 &&
                    latestErrorDisplay.Any(delegate(SessionSnapshot snapshot)
                    {
                        return snapshot.ThreadId == "new-error";
                    }), "latest Windows error remains visible with active sessions");

                SessionSnapshot metadataOnly = new SessionSnapshot
                {
                    ThreadId = "metadata-only",
                    ProjectName = "Codex",
                    State = HaloState.Idle,
                    Action = "Ready",
                    LastEventUtc = supersessionNow,
                    Active = false,
                    Agent = AgentKind.Codex
                };
                List<SessionSnapshot> metadataDisplay =
                    CodexSessionMonitor.WithoutSupersededErrors(
                        new[] { oldError, metadataOnly });
                Assert(metadataDisplay.Any(delegate(SessionSnapshot snapshot)
                {
                    return snapshot.ThreadId == "old-error";
                }), "metadata-only Windows session does not suppress old error");

                HaloSettings presenceSettings = new HaloSettings
                {
                    InstalledAt = supersessionNow.AddHours(-1).ToString("o",
                        CultureInfo.InvariantCulture)
                };
                using (CodexSessionMonitor presenceMonitor = new CodexSessionMonitor())
                {
                    AggregateSnapshot standby = presenceMonitor.GetAggregate(
                        presenceSettings, true);
                    Assert(standby.State == HaloState.Done &&
                        standby.Presence == AgentPresenceState.Standby &&
                        standby.TurnPhase == AgentTurnPhase.None &&
                        standby.Label == "STANDBY",
                        "running Codex without an active turn becomes normalized standby");
                    Assert(HaloWindow.ShouldShowGreenStandby(standby, false),
                        "standby aggregate selects the steady-green visual instead of completion breathing");
                    AggregateSnapshot offline = presenceMonitor.GetAggregate(
                        presenceSettings, false);
                    Assert(offline.State == HaloState.Idle &&
                        offline.Presence == AgentPresenceState.Offline &&
                        offline.TurnPhase == AgentTurnPhase.None,
                        "stopped Codex becomes normalized offline");
                    Assert(!HaloWindow.ShouldShowGreenStandby(offline, false) &&
                        !HaloWindow.ShouldShowGreenStandby(standby, true),
                        "offline and preview aggregates cannot select the standby visual");
                }
                SessionSnapshot recentActive = new SessionSnapshot
                {
                    ThreadId = "recent-active",
                    State = HaloState.Working,
                    Active = true,
                    LastEventUtc = supersessionNow.AddMinutes(-1)
                };
                Assert(CodexSessionMonitor.IsSessionVisible(recentActive,
                    presenceSettings, true, supersessionNow),
                    "recent active session remains visible while Codex runs");
                Assert(!CodexSessionMonitor.IsSessionVisible(recentActive,
                    presenceSettings, false, supersessionNow),
                    "active session cannot keep Codex online after its process exits");
                recentActive.LastEventUtc = supersessionNow.AddMinutes(-11);
                Assert(!CodexSessionMonitor.IsSessionVisible(recentActive,
                    presenceSettings, true, supersessionNow),
                    "stale active session cannot leave the halo permanently working");

                SessionSnapshot recentCompleted = new SessionSnapshot
                {
                    ThreadId = "recent-completed",
                    State = HaloState.Done,
                    Action = "Complete",
                    LastEventUtc = supersessionNow.AddMinutes(-4).AddSeconds(-59),
                    CompletedUtc = supersessionNow.AddMinutes(-4).AddSeconds(-59),
                    Active = false,
                    Agent = AgentKind.Codex
                };
                Assert(CodexSessionMonitor.IsSessionVisible(recentCompleted,
                    presenceSettings, true, supersessionNow),
                    "recent completion remains visible for five minutes while Codex runs");
                recentCompleted.CompletedUtc = supersessionNow.AddMinutes(-5);
                Assert(!CodexSessionMonitor.IsSessionVisible(recentCompleted,
                    presenceSettings, true, supersessionNow),
                    "completion expires exactly at five minutes");
                recentCompleted.CompletedUtc = supersessionNow.AddMinutes(-4);
                Assert(!CodexSessionMonitor.IsSessionVisible(recentCompleted,
                    presenceSettings, false, supersessionNow),
                    "completed session becomes offline immediately when Codex exits");
                string completionFixtureRoot = Path.Combine(Path.GetTempPath(),
                    "agent-halo-completion-fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(completionFixtureRoot);
                try
                {
                    DateTime recentCompletionUtc = supersessionNow.AddMinutes(-4).
                        AddSeconds(-59);
                    string recentFixture = Path.Combine(completionFixtureRoot,
                        "recent-completion.jsonl");
                    File.WriteAllText(recentFixture,
                        "{\"timestamp\":\"" + recentCompletionUtc.AddSeconds(-1).
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"recent-fixture\",\"cwd\":\"C:\\\\work\\\\fixture\"}}\n" +
                        "{\"timestamp\":\"" + recentCompletionUtc.
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n" +
                        "{\"timestamp\":\"" + recentCompletionUtc.
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                        Encoding.UTF8);
                    SessionTracker recentCompletionTracker =
                        new SessionTracker(recentFixture);
                    Assert(recentCompletionTracker.Snapshot.State == HaloState.Done &&
                        recentCompletionTracker.Snapshot.CompletedUtc ==
                            recentCompletionUtc,
                        "JSONL task_complete records its completion timestamp");
                    List<SessionSnapshot> parsedRecentCompletion =
                        CodexSessionMonitor.SelectVisibleSessions(
                            new[] { recentCompletionTracker.Snapshot },
                            presenceSettings, true, supersessionNow);
                    Assert(parsedRecentCompletion.Count == 1 &&
                        parsedRecentCompletion[0].State == HaloState.Done,
                        "parsed 4m59 completion reaches the visible completed state");
                    Assert(CodexSessionMonitor.SelectVisibleSessions(
                        new[] { recentCompletionTracker.Snapshot },
                        presenceSettings, false, supersessionNow).Count == 0,
                        "parsed completion is removed when the desktop process stops");

                    DateTime expiredCompletionUtc = supersessionNow.AddMinutes(-5);
                    string expiredFixture = Path.Combine(completionFixtureRoot,
                        "expired-completion.jsonl");
                    File.WriteAllText(expiredFixture,
                        "{\"timestamp\":\"" + expiredCompletionUtc.AddSeconds(-1).
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"expired-fixture\",\"cwd\":\"C:\\\\work\\\\fixture\"}}\n" +
                        "{\"timestamp\":\"" + expiredCompletionUtc.
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n" +
                        "{\"timestamp\":\"" + expiredCompletionUtc.
                            ToString("o", CultureInfo.InvariantCulture) +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n",
                        Encoding.UTF8);
                    SessionTracker expiredCompletionTracker =
                        new SessionTracker(expiredFixture);
                    Assert(expiredCompletionTracker.Snapshot.State == HaloState.Done &&
                        CodexSessionMonitor.SelectVisibleSessions(
                            new[] { expiredCompletionTracker.Snapshot },
                            presenceSettings, true, supersessionNow).Count == 0,
                        "parsed completion expires exactly at five minutes into standby selection");
                }
                finally
                {
                    if (Directory.Exists(completionFixtureRoot))
                    {
                        Directory.Delete(completionFixtureRoot, true);
                    }
                }
                foreach (HaloState replacementState in new[] {
                    HaloState.Thinking, HaloState.Working, HaloState.Attention,
                    HaloState.Error })
                {
                    SessionSnapshot replacement = new SessionSnapshot
                    {
                        ThreadId = "replacement-" + replacementState.ToString(),
                        State = replacementState,
                        Active = replacementState != HaloState.Error,
                        LastEventUtc = supersessionNow,
                        Agent = AgentKind.Codex
                    };
                    List<SessionSnapshot> prioritized =
                        CodexSessionMonitor.SelectVisibleSessions(
                            new[] { recentCompleted, replacement },
                            presenceSettings, true, supersessionNow);
                    Assert(prioritized.Count == 2 &&
                        prioritized[0].State == replacementState,
                        "new " + replacementState.ToString() +
                        " state takes precedence over a recent completion");
                }

                string watcherRoot = Path.Combine(Path.GetTempPath(),
                    "agent-halo-session-watch-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(watcherRoot);
                using (CodexSessionMonitor watcherMonitor =
                    new CodexSessionMonitor(watcherRoot, false))
                {
                    watcherMonitor.Start();
                    string watcherSession = Path.Combine(watcherRoot,
                        "rollout-" + Guid.NewGuid().ToString() + ".jsonl");
                    File.WriteAllText(watcherSession,
                        "{\"timestamp\":\"" + DateTime.UtcNow.ToString("o") +
                        "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"watcher-test\"," +
                        "\"cwd\":\"C:\\\\work\\\\watcher\"}}\n" +
                        "{\"timestamp\":\"" + DateTime.UtcNow.ToString("o") +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                        Encoding.UTF8);
                    DateTime watcherDeadline = DateTime.UtcNow.AddSeconds(3);
                    AggregateSnapshot watcherAggregate = null;
                    while (DateTime.UtcNow < watcherDeadline)
                    {
                        watcherAggregate = watcherMonitor.GetAggregate(
                            presenceSettings, true);
                        if (watcherAggregate.State == HaloState.Thinking) break;
                        Thread.Sleep(50);
                    }
                    Assert(watcherAggregate != null &&
                        watcherAggregate.State == HaloState.Thinking &&
                        watcherAggregate.EvidenceSource ==
                            AgentEvidenceSource.SessionJsonl,
                        "session watcher discovers a new active turn incrementally");

                    watcherMonitor.Stop();
                    string pausedWatcherSession = Path.Combine(watcherRoot,
                        "rollout-paused-" + Guid.NewGuid().ToString() + ".jsonl");
                    File.WriteAllText(pausedWatcherSession,
                        "{\"timestamp\":\"" + DateTime.UtcNow.ToString("o") +
                        "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"watcher-paused\"," +
                        "\"cwd\":\"C:\\\\work\\\\paused-watcher\"}}\n" +
                        "{\"timestamp\":\"" + DateTime.UtcNow.ToString("o") +
                        "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n",
                        Encoding.UTF8);
                    Thread.Sleep(400);
                    Assert(!watcherMonitor.GetAllRecent().Any(
                        delegate(SessionSnapshot snapshot)
                        {
                            return snapshot.ProjectName == "paused-watcher";
                        }),
                        "stopped session monitor does not consume watcher events");

                    watcherMonitor.Start();
                    DateTime resumeDeadline = DateTime.UtcNow.AddSeconds(3);
                    bool resumedSessionFound = false;
                    while (DateTime.UtcNow < resumeDeadline)
                    {
                        resumedSessionFound = watcherMonitor.GetAllRecent().Any(
                            delegate(SessionSnapshot snapshot)
                            {
                                return snapshot.ProjectName == "paused-watcher";
                            });
                        if (resumedSessionFound) break;
                        Thread.Sleep(50);
                    }
                    Assert(resumedSessionFound,
                        "restarted session monitor performs catch-up discovery");
                }
                Directory.Delete(watcherRoot, true);

                RunProviderCoordinatorChecks();
                RunDeepSeekAutomaticSetupChecks();
                RunDeepSeekIntegrationStateChecks();
                File.Delete(temp);
                File.WriteAllText(outputPath,
                    "PASS\nLifecycle, Codex and DSH provider coordination, DSH automatic setup and task details, topmost guard, usage metrics, panel formatting, and animation checks passed.\n",
                    Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "FAIL\n" + ex.ToString(), Encoding.UTF8);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "AGENTHALO_DIAGNOSTIC_APP_DIRECTORY",
                    previousDiagnosticDirectory);
                Environment.SetEnvironmentVariable("AGENTHALO_TEST_MODE",
                    previousTestMode);
                TryDeleteSelfTestAppDirectory(diagnosticAppDirectory);
            }
        }

        private static void RunDeepSeekAutomaticSetupChecks()
        {
            string root = Path.Combine(SettingsStorage.AppDirectory, "deepseek-first-run");
            string profile = Path.Combine(root, "profile");
            string app = Path.Combine(root, "fresh user's app");
            DeepSeekHarnessSetup setup = new DeepSeekHarnessSetup(profile, app);
            Assert(!setup.TryInstall() && !Directory.Exists(profile),
                "DSH setup waits for first desktop launch");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "package.json"), "{}");
            File.WriteAllText(Path.Combine(profile, "pnpm-workspace.yaml"), "packages: [.]\n");
            string patchPath = Path.Combine(profile, "cordis.patch.yml");
            File.WriteAllText(patchPath, "# DSH empty profile\r\n[]\r\n");
            string profileLock = Path.Combine(profile, "lock");
            File.WriteAllText(profileLock, "initializing");
            Assert(!setup.TryInstall(), "DSH setup respects desktop initialization lock");
            File.Delete(profileLock);
            Assert(setup.TryInstall(), "DSH first-run observer installs from embedded resource");
            string observer = Path.Combine(app, "integrations", "deepseek-harness", "observer", "index.mjs");
            string registered = File.ReadAllText(patchPath);
            Assert(File.Exists(observer) && File.ReadAllText(observer).Contains("export function apply(ctx)") &&
                registered.Contains("fresh user''s app") && !registered.Contains("[]") &&
                registered.StartsWith("# DSH empty profile\r\n"),
                "DSH empty profile becomes valid registration with quoted local path");
            DeepSeekHarnessSetup repeated = new DeepSeekHarnessSetup(profile, app);
            Assert(repeated.TryInstall() && File.ReadAllText(patchPath) == registered,
                "DSH repeated startup preserves the existing registration");

            string other = "# keep my plugin\n- insert:\n    - id: my-plugin\n      name: my-module\n";
            string old = other + "- insert:\n    - id: " + DeepSeekHarnessSetup.PluginId +
                "\n      name: 'E:/old/index.mjs' # local observer\n" +
                "- id: my-plugin\n  disabled: false\n";
            string migrated = DeepSeekHarnessSetup.RegisterObserver(old, observer);
            Assert(migrated.StartsWith(other) && migrated.EndsWith("# local observer\n- id: my-plugin\n  disabled: false\n") &&
                !migrated.Contains("E:/old/") && DeepSeekHarnessSetup.RegisterObserver(migrated, observer) == migrated,
                "DSH path migration preserves other plugins and comments without duplicate insert");
            HaloSettings fresh = new HaloSettings();
            Assert(DeepSeekHarnessSetup.EnableMonitoring(fresh) &&
                fresh.IsAgentEnabled(AgentKind.DeepSeekHarness) &&
                fresh.FocusedAgent == "deepseek-harness",
                "DSH first setup enables the provider");
            HaloSettings previouslyConfigured = new HaloSettings();
            previouslyConfigured.EnabledAgents.Add("deepseek-harness");
            Assert(DeepSeekHarnessSetup.EnableMonitoring(previouslyConfigured) &&
                previouslyConfigured.FocusedAgent == "codex",
                "DSH manual integrations retain the existing agent selection");
            fresh.FocusedAgent = "codex";
            fresh.EnabledAgents.Remove("deepseek-harness");
            Assert(!DeepSeekHarnessSetup.EnableMonitoring(fresh) && fresh.FocusedAgent == "codex" &&
                !fresh.IsAgentEnabled(AgentKind.DeepSeekHarness),
                "DSH later startup respects the saved choice and explicit removal");
        }

        private static void RunDeepSeekIntegrationStateChecks()
        {
            AgentIntegrationState[] states =
            {
                AgentIntegrationState.NotConfigured,
                AgentIntegrationState.NotRequired,
                AgentIntegrationState.Healthy,
                AgentIntegrationState.Stale,
                AgentIntegrationState.Broken
            };
            DateTime lastEventUtc = DateTime.UtcNow.AddMinutes(-1);
            for (int i = 0; i < states.Length; i++)
            {
                AgentIntegrationState state = states[i];
                DeepSeekHarnessReadResult source =
                    new DeepSeekHarnessReadResult
                    {
                        IntegrationState = state,
                        StatusDetailKey = state ==
                            AgentIntegrationState.NotConfigured
                                ? "status.deepseek.not_configured"
                                : "status.deepseek.unknown",
                        LastEventUtc = lastEventUtc,
                        Broken = state == AgentIntegrationState.Broken
                    };
                string selectedRootId;
                AgentProviderSnapshot snapshot =
                    DeepSeekHarnessSnapshotReducer.Build(source,
                        new HaloSettings(), DateTime.UtcNow, String.Empty,
                        out selectedRootId);
                Assert(snapshot.Integration != null &&
                    snapshot.Integration.State == state &&
                    snapshot.Integration.LastEventUtc == lastEventUtc,
                    "DSH reducer preserves integration state " +
                        state.ToString());
            }
        }

        private static void TryDeleteSelfTestAppDirectory(string directory)
        {
            try
            {
                string full = Path.GetFullPath(directory);
                string temp = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(full);
                string parent = Path.GetDirectoryName(full);
                bool directChild = String.Equals(parent, temp,
                    StringComparison.OrdinalIgnoreCase);
                bool containsReparsePoint = false;
                if (Directory.Exists(full))
                {
                    DirectoryInfo root = new DirectoryInfo(full);
                    containsReparsePoint = (root.Attributes &
                        FileAttributes.ReparsePoint) != 0 ||
                        root.EnumerateFileSystemInfos("*",
                            SearchOption.AllDirectories).Any(delegate(
                                FileSystemInfo item)
                        {
                            return (item.Attributes &
                                FileAttributes.ReparsePoint) != 0;
                        });
                }
                if (directChild &&
                    name.StartsWith("AgentHalo-self-test-app-",
                        StringComparison.Ordinal) &&
                    name.Length == "AgentHalo-self-test-app-".Length + 32 &&
                    Directory.Exists(full) && !containsReparsePoint)
                {
                    Directory.Delete(full, true);
                }
            }
            catch
            {
            }
        }

        public static int WriteLiveSnapshot(string outputPath)
        {
            try
            {
                HaloSettings settings = SettingsStorage.Load();
                using (CodexSessionMonitor monitor = new CodexSessionMonitor())
                {
                    monitor.Start();
                    AggregateSnapshot aggregate = monitor.GetAggregate(settings);
                    StringBuilder report = new StringBuilder();
                    report.AppendLine(aggregate.Label);
                    report.AppendLine(aggregate.Detail);
                    report.AppendLine("Presence: " + aggregate.Presence);
                    report.AppendLine("Turn: " + aggregate.TurnPhase);
                    report.AppendLine("Activity: " + aggregate.Activity);
                    report.AppendLine("Attention: " + aggregate.AttentionReason);
                    report.AppendLine("Failure: " + aggregate.FailureSeverity);
                    report.AppendLine("Evidence: " + aggregate.EvidenceSource +
                        " / " + aggregate.EvidenceKind);
                    report.AppendLine("Sessions: " + aggregate.Sessions.Count.ToString(
                        CultureInfo.InvariantCulture));
                    foreach (SessionSnapshot session in aggregate.Sessions)
                    {
                        report.AppendLine(String.Format(CultureInfo.InvariantCulture,
                            "{0} | {1} | {2}", session.ProjectName,
                            CodexSessionMonitor.StateLabel(session.State), session.Action));
                    }
                    File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "FAIL\n" + ex.ToString(), Encoding.UTF8);
                return 1;
            }
        }


        private static void RunProviderCoordinatorChecks()
        {
            Assert(!CodexUsageMonitor.InstanceCreatedForDiagnostics,
                "provider checks start without the Codex usage singleton");
            FakeAgentProvider fakeProvider = new FakeAgentProvider();
            AgentProviderCatalog catalog = new AgentProviderCatalog(new[]
            {
                new AgentProviderDescriptor
                {
                    Kind = AgentKind.Codex,
                    Key = "codex",
                    DisplayName = "Codex",
                    Order = 0,
                    Capabilities = fakeProvider.Capabilities,
                    CreateProvider = delegate { return fakeProvider; }
                }
            });
            Assert(catalog.Descriptors.Count == 1 &&
                catalog.Descriptors[0].Key == "codex",
                "provider catalog owns the Codex registration");
            FakeAgentProvider wrongProvider = new FakeAgentProvider();
            foreach (Func<IAgentProvider> factory in new Func<IAgentProvider>[]
            {
                delegate { return null; },
                delegate { throw new InvalidOperationException("factory failed"); },
                delegate { return wrongProvider; }
            })
            {
                AgentProviderCatalog invalidCatalog = new AgentProviderCatalog(new[]
                {
                    new AgentProviderDescriptor
                    {
                        Kind = AgentKind.DeepSeekHarness,
                        Key = "deepseek-harness",
                        CreateProvider = factory
                    }
                });
                bool rejected = false;
                try { invalidCatalog.Create(AgentKind.DeepSeekHarness); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "provider catalog rejects a failed factory");
            }
            Assert(wrongProvider.DisposeCount == 1,
                "provider catalog disposes a wrong-kind result once");

            UsageMetrics quotaMetrics = new UsageMetrics
            {
                HasFiveHour = true,
                FiveHourUsedPercent = 25,
                ContextInputTokens = 100,
                ContextWindowTokens = 1000
            };
            AgentDetailsSnapshot quotaDetails =
                CodexAgentProvider.CreateDetailsSnapshot(quotaMetrics,
                    new CodexCustomApiMetrics { IsCustomApi = false });
            Assert(quotaDetails.Mode == AgentDetailsMode.Quota &&
                quotaDetails.Usage == quotaMetrics,
                "Codex provider maps official usage to generic quota details");
            AgentDetailsSnapshot informationDetails =
                CodexAgentProvider.CreateDetailsSnapshot(null,
                    new CodexCustomApiMetrics
                    {
                        IsCustomApi = true,
                        ProjectName = "AgentHalo",
                        Model = "custom-model",
                        Provider = "Private API",
                        InputTokens = 1200,
                        OutputTokens = 80,
                        ContextTokens = 1200,
                        ContextWindowTokens = 16000
                    });
            Assert(informationDetails.Mode == AgentDetailsMode.Information &&
                informationDetails.ProjectName == "AgentHalo" &&
                informationDetails.ModelName == "custom-model" &&
                informationDetails.ProviderName == "Private API" &&
                informationDetails.HasContext,
                "Codex provider maps custom API data to generic information details");

            DateTime failureUtc = DateTime.UtcNow;
            HaloSettings failureSettings = new HaloSettings();
            AggregateSnapshot failureAggregate = new AggregateSnapshot
            {
                State = HaloState.Done,
                Label = "STANDBY",
                Presence = AgentPresenceState.Standby,
                Sessions = new List<SessionSnapshot>()
            };
            CodexAgentProvider.ApplyApplicationFailure(failureAggregate,
                failureSettings, true, true, "Application failure", failureUtc);
            Assert(failureAggregate.State == HaloState.Error &&
                failureAggregate.Sessions.Count == 1 &&
                failureAggregate.Sessions[0].EvidenceSource ==
                    AgentEvidenceSource.DiagnosticSqlite,
                "Codex provider applies a fresh application failure");
            failureSettings.AcknowledgedErrorAt = failureUtc.ToString("o");
            AggregateSnapshot acknowledgedFailure = new AggregateSnapshot
            {
                State = HaloState.Done,
                Label = "STANDBY",
                Presence = AgentPresenceState.Standby,
                Sessions = new List<SessionSnapshot>()
            };
            CodexAgentProvider.ApplyApplicationFailure(acknowledgedFailure,
                failureSettings, true, true, "Application failure", failureUtc);
            Assert(acknowledgedFailure.State == HaloState.Done &&
                acknowledgedFailure.Sessions.Count == 0,
                "Codex provider suppresses an acknowledged application failure");

            int changedCount = 0;
            AgentMonitorCoordinator coordinator =
                new AgentMonitorCoordinator(catalog, AgentKind.Codex);
            coordinator.Changed += delegate { changedCount++; };
            coordinator.Start();
            coordinator.Start();
            Assert(fakeProvider.StartCount == 1,
                "coordinator start is idempotent");
            fakeProvider.RaiseChanged();
            Assert(changedCount == 1,
                "coordinator forwards one active provider callback");
            Assert(coordinator.Refresh() && fakeProvider.RefreshCount == 1,
                "coordinator delegates refresh");
            AgentProviderSnapshot snapshot = coordinator.Read(
                new HaloSettings(), DateTime.UtcNow);
            Assert(snapshot == fakeProvider.Snapshot &&
                fakeProvider.ReadCount == 1,
                "coordinator returns the focused provider snapshot");
            Assert(coordinator.IsForeground(new IntPtr(1)) &&
                fakeProvider.ForegroundCount == 1,
                "coordinator delegates foreground detection");
            Assert(coordinator.TryActivateWindow() &&
                fakeProvider.ActivateWindowCount == 1,
                "coordinator delegates window activation");

            coordinator.Stop();
            coordinator.Stop();
            Assert(fakeProvider.StopCount == 1,
                "coordinator stop is idempotent");
            fakeProvider.RaiseChanged();
            Assert(changedCount == 1,
                "stopped provider callback cannot refresh the UI");
            coordinator.Start();
            fakeProvider.RaiseChanged();
            Assert(fakeProvider.StartCount == 2 && changedCount == 2,
                "restart does not duplicate provider subscriptions");
            coordinator.Dispose();
            Assert(fakeProvider.StopCount == 2 &&
                fakeProvider.DisposeCount == 1,
                "coordinator disposes the provider once");
            fakeProvider.RaiseChanged();
            Assert(changedCount == 2,
                "disposed coordinator detaches provider callbacks");

            FakeAgentProvider flakyProvider = new FakeAgentProvider();
            flakyProvider.StartFailuresRemaining = 1;
            AgentMonitorCoordinator flakyCoordinator =
                new AgentMonitorCoordinator(new AgentProviderCatalog(new[]
                {
                    new AgentProviderDescriptor
                    {
                        Kind = AgentKind.Codex,
                        Key = "codex",
                        DisplayName = "Codex",
                        CreateProvider = delegate { return flakyProvider; }
                    }
                }), AgentKind.Codex);
            bool startFailed = false;
            try
            {
                flakyCoordinator.Start();
            }
            catch (InvalidOperationException)
            {
                startFailed = true;
            }
            Assert(startFailed && flakyProvider.StartCount == 1 &&
                flakyProvider.StopCount == 1,
                "coordinator rolls back a partially failed start");
            flakyCoordinator.Start();
            Assert(flakyProvider.StartCount == 2,
                "coordinator can retry after a failed start");
            flakyProvider.StopFailuresRemaining = 1;
            bool stopFailed = false;
            try
            {
                flakyCoordinator.Stop();
            }
            catch (InvalidOperationException)
            {
                stopFailed = true;
            }
            Assert(stopFailed,
                "coordinator surfaces a provider stop failure");
            flakyCoordinator.Start();
            Assert(flakyProvider.StartCount == 3,
                "coordinator can restart after a failed stop");
            flakyCoordinator.Dispose();

            RunAgentSettingsChecks();
            RunDefaultCodexIsolationChecks();
            RunAgentSwitchTransactionChecks();
            RunAgentSwitchFailureChecks();
            RunAgentPauseChecks();
            RunAgentDetailsIsolationChecks();
            Assert(!CodexUsageMonitor.InstanceCreatedForDiagnostics,
                "provider checks remain offline and credential-free");
        }

        private static void RunAgentSettingsChecks()
        {
            AgentProviderCatalog catalog = new AgentProviderCatalog();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Assert(catalog.Descriptors.Single(delegate(
                    AgentProviderDescriptor descriptor)
                {
                    return descriptor.Kind == AgentKind.Codex;
                }).EnabledByDefault &&
                !catalog.Descriptors.Single(delegate(
                    AgentProviderDescriptor descriptor)
                {
                    return descriptor.Kind == AgentKind.DeepSeekHarness;
                }).EnabledByDefault,
                "Codex is the only provider enabled by default");

            HaloSettings legacy = serializer.Deserialize<HaloSettings>(
                "{\"FocusedAgent\":\"codex\"}");
            legacy.NormalizeAgentSelection(catalog);
            Assert(legacy.FocusedAgent == "codex" &&
                legacy.EnabledAgents.SequenceEqual(new[] { "codex" }) &&
                catalog.DefaultEnabledKeys.SequenceEqual(
                    new[] { "codex" }),
                "old and new settings preserve Codex-only defaults");

            HaloSettings invalid = new HaloSettings
            {
                FocusedAgent = "unknown-agent",
                EnabledAgents = new List<string> { "unknown-agent" }
            };
            Assert(invalid.NormalizeAgentSelection(catalog) &&
                invalid.FocusedAgent == "codex" &&
                invalid.EnabledAgents.SequenceEqual(new[] { "codex" }),
                "invalid agent settings fall back to Codex");

            HaloSettings canonical = new HaloSettings
            {
                FocusedAgent = " DEEPSEEK-HARNESS ",
                EnabledAgents = new List<string>
                {
                    " deepseek-harness ", "CODEX", "codex", "missing"
                }
            };
            canonical.NormalizeAgentSelection(catalog);
            Assert(canonical.FocusedAgent == "deepseek-harness" &&
                canonical.EnabledAgents.SequenceEqual(new[]
                {
                    "codex", "deepseek-harness"
                }), "agent settings are canonical and de-duplicated");

            HaloSettings disabledFocus = new HaloSettings
            {
                FocusedAgent = "deepseek-harness",
                EnabledAgents = new List<string> { "codex" }
            };
            disabledFocus.NormalizeAgentSelection(catalog);
            Assert(disabledFocus.FocusedAgent == "codex",
                "disabled focused agent falls back to the first enabled agent");

            HaloSettings persisted = new HaloSettings
            {
                FocusedAgent = "deepseek-harness",
                EnabledAgents = new List<string>
                {
                    "codex", "deepseek-harness"
                }
            };
            HaloSettings restarted = serializer.Deserialize<HaloSettings>(
                serializer.Serialize(persisted));
            restarted.NormalizeAgentSelection(catalog);
            Assert(restarted.FocusedAgent == "deepseek-harness",
                "focused agent survives an in-memory restart round trip");

            HaloSettings removedProvider = serializer.Deserialize<HaloSettings>(
                "{\"FocusedAgent\":\"antigravity\",\"EnabledAgents\":[\"codex\",\"antigravity\",\"deepseek-harness\"]}");
            removedProvider.NormalizeAgentSelection(catalog);
            Assert(removedProvider.FocusedAgent == "codex" &&
                removedProvider.EnabledAgents.SequenceEqual(new[]
                {
                    "codex", "deepseek-harness"
                }),
                "saved selections discard the removed provider and retain Codex and DSH");

            HaloSettings remainingDeepSeek = serializer.Deserialize<HaloSettings>(
                "{\"FocusedAgent\":\" ANTIGRAVITY \",\"EnabledAgents\":[\"deepseek-harness\"],\"DeepSeekHarnessSetupComplete\":true}");
            remainingDeepSeek.NormalizeAgentSelection(catalog);
            Assert(remainingDeepSeek.FocusedAgent == "deepseek-harness" &&
                remainingDeepSeek.EnabledAgents.SequenceEqual(new[]
                {
                    "deepseek-harness"
                }) && remainingDeepSeek.DeepSeekHarnessSetupComplete,
                "retired focus preserves DSH-only monitoring and completed setup");

            HaloSettings acknowledgements = new HaloSettings();
            DateTime codexDone = DateTime.UtcNow.AddMinutes(-3);
            DateTime deepSeekDone = DateTime.UtcNow.AddMinutes(-2);
            acknowledgements.Acknowledge(AgentKind.Codex, "same-id",
                codexDone);
            acknowledgements.Acknowledge(AgentKind.DeepSeekHarness, "same-id",
                deepSeekDone);
            Assert(acknowledgements.GetAcknowledgedUtc(AgentKind.Codex,
                    "same-id") == codexDone.ToUniversalTime() &&
                acknowledgements.GetAcknowledgedUtc(
                    AgentKind.DeepSeekHarness, "same-id") ==
                    deepSeekDone.ToUniversalTime(),
                "completion acknowledgements are agent scoped");

            DateTime legacyDone = DateTime.UtcNow.AddMinutes(-4);
            acknowledgements.Acknowledged["legacy-id"] =
                legacyDone.ToUniversalTime().ToString("o");
            Assert(acknowledgements.GetAcknowledgedUtc(AgentKind.Codex,
                    "legacy-id") == legacyDone.ToUniversalTime() &&
                acknowledgements.GetAcknowledgedUtc(
                    AgentKind.DeepSeekHarness, "legacy-id") ==
                    DateTime.MinValue,
                "legacy completion acknowledgement only migrates to Codex");

            DateTime legacyError = DateTime.UtcNow.AddMinutes(-5);
            DateTime deepSeekError = DateTime.UtcNow.AddMinutes(-1);
            acknowledgements.AcknowledgedErrorAt =
                legacyError.ToUniversalTime().ToString("o");
            acknowledgements.AcknowledgeError(AgentKind.DeepSeekHarness,
                deepSeekError);
            Assert(acknowledgements.GetAcknowledgedErrorUtc(
                    AgentKind.Codex) == legacyError.ToUniversalTime() &&
                acknowledgements.GetAcknowledgedErrorUtc(
                    AgentKind.DeepSeekHarness) ==
                    deepSeekError.ToUniversalTime(),
                "error acknowledgements are agent scoped");
        }

        private static void RunDefaultCodexIsolationChecks()
        {
            SwitchProbe probe = new SwitchProbe();
            SwitchingFakeAgentProvider codex;
            SwitchingFakeAgentProvider deepSeekHarness;
            AgentProviderCatalog catalog = CreateSwitchCatalog(probe,
                out codex, out deepSeekHarness);
            HaloSettings settings = new HaloSettings();
            settings.NormalizeAgentSelection(catalog);
            AgentMonitorCoordinator coordinator =
                new AgentMonitorCoordinator(catalog, catalog.DefaultKind);
            coordinator.Start();
            AgentProviderSnapshot snapshot = coordinator.Read(settings,
                DateTime.UtcNow);
            Assert(catalog.DefaultKind == AgentKind.Codex &&
                settings.FocusedAgent == "codex" &&
                settings.EnabledAgents.SequenceEqual(new[] { "codex" }) &&
                probe.CodexCreateCount == 1 &&
                probe.DeepSeekHarnessCreateCount == 0 &&
                codex.IsActive && !deepSeekHarness.IsActive &&
                snapshot.Aggregate.FocusedAgent == AgentKind.Codex,
                "default startup creates and reads only the Codex provider");
            coordinator.Dispose();
            Assert(probe.ActiveCount == 0 && codex.DisposeCount == 1 &&
                deepSeekHarness.DisposeCount == 0,
                "default Codex shutdown never instantiates DeepSeek Harness");
        }

        private static void RunAgentSwitchTransactionChecks()
        {
            SwitchProbe probe = new SwitchProbe();
            SwitchingFakeAgentProvider codex;
            SwitchingFakeAgentProvider deepSeekHarness;
            AgentProviderCatalog catalog = CreateSwitchCatalog(probe,
                out codex, out deepSeekHarness);
            HaloSettings settings = EnabledSwitchSettings(catalog);
            AgentMonitorCoordinator coordinator =
                new AgentMonitorCoordinator(catalog, AgentKind.Codex);
            List<AgentCoordinatorChangedEventArgs> changes =
                new List<AgentCoordinatorChangedEventArgs>();
            coordinator.Changed += delegate(object sender,
                AgentCoordinatorChangedEventArgs e)
            {
                changes.Add(e);
            };

            coordinator.Start();
            Assert(probe.ActiveCount == 1 && codex.IsActive &&
                !deepSeekHarness.IsActive && codex.SubscriberCount == 1,
                "coordinator starts only the focused provider");

            Action delayedCodexCallback = codex.CaptureChanged();
            codex.RaiseChanged();
            Assert(changes.Count == 1 && !changes[0].FocusChanged &&
                coordinator.IsCurrent(changes[0]),
                "current provider callback carries its generation");
            AgentCoordinatorChangedEventArgs queuedOldChange = changes[0];

            probe.Sequence.Clear();
            codex.RaiseChangedOnStop = true;
            int persistCount = 0;
            AgentProviderSnapshot firstSnapshot;
            bool switched = coordinator.TrySwitch(
                AgentKind.DeepSeekHarness, settings, DateTime.UtcNow,
                delegate
                {
                    persistCount++;
                    probe.Sequence.Add("persist");
                    return true;
                }, out firstSnapshot);
            Assert(switched && persistCount == 1 &&
                coordinator.FocusedKind == AgentKind.DeepSeekHarness &&
                settings.FocusedAgent == "deepseek-harness" &&
                firstSnapshot.Aggregate.FocusedAgent ==
                    AgentKind.DeepSeekHarness,
                "coordinator commits the candidate snapshot and focus");
            Assert(probe.Sequence.IndexOf("codex.stop") >= 0 &&
                probe.Sequence.IndexOf("codex.stop") <
                    probe.Sequence.IndexOf("deepseek-harness.start") &&
                probe.Sequence.IndexOf("deepseek-harness.start") <
                    probe.Sequence.IndexOf("deepseek-harness.refresh") &&
                probe.Sequence.IndexOf("deepseek-harness.refresh") <
                    probe.Sequence.IndexOf("deepseek-harness.read") &&
                probe.Sequence.IndexOf("deepseek-harness.read") <
                    probe.Sequence.IndexOf("persist"),
                "agent switch orders stop, preflight, persistence, then commit");
            Assert(changes.Count == 2 && changes[1].FocusChanged &&
                changes[1].Kind == AgentKind.DeepSeekHarness &&
                coordinator.IsCurrent(changes[1]),
                "successful switch publishes one focused generation");

            delayedCodexCallback();
            Assert(changes.Count == 2 &&
                !coordinator.IsCurrent(queuedOldChange),
                "old and queued provider callbacks are generation filtered");
            Assert(probe.ActiveCount == 1 && deepSeekHarness.IsActive &&
                !codex.IsActive && deepSeekHarness.SubscriberCount == 1 &&
                codex.SubscriberCount == 0,
                "successful switch leaves one active subscribed provider");

            codex.ForegroundResult = true;
            deepSeekHarness.ForegroundResult = true;
            codex.ActivationResult = true;
            deepSeekHarness.ActivationResult = true;
            int codexForegroundBefore = codex.ForegroundCount;
            int codexActivationBefore = codex.ActivateWindowCount;
            Assert(coordinator.IsForeground(new IntPtr(7)) &&
                coordinator.TryActivateWindow() &&
                deepSeekHarness.ForegroundCount == 1 &&
                deepSeekHarness.ActivateWindowCount == 1 &&
                codex.ForegroundCount == codexForegroundBefore &&
                codex.ActivateWindowCount == codexActivationBefore,
                "window operations dispatch only to the focused provider");

            Assert(coordinator.TrySwitch(AgentKind.Codex, settings,
                    DateTime.UtcNow, delegate { return true; },
                    out firstSnapshot) &&
                coordinator.FocusedKind == AgentKind.Codex,
                "coordinator can switch back to the cached Codex provider");
            int startBefore = codex.StartCount;
            int stopBefore = codex.StopCount;
            int focusChangesBefore = changes.Count(delegate(
                AgentCoordinatorChangedEventArgs e)
            {
                return e.FocusChanged;
            });
            int sameTargetPersistCount = 0;
            Assert(coordinator.TrySwitch(AgentKind.Codex, settings,
                    DateTime.UtcNow, delegate
                    {
                        sameTargetPersistCount++;
                        return true;
                    }, out firstSnapshot) &&
                sameTargetPersistCount == 0 &&
                codex.StartCount == startBefore &&
                codex.StopCount == stopBefore &&
                changes.Count(delegate(AgentCoordinatorChangedEventArgs e)
                {
                    return e.FocusChanged;
                }) == focusChangesBefore,
                "same-target selection has no lifecycle or persistence churn");
            Assert(coordinator.TrySwitch(AgentKind.DeepSeekHarness, settings,
                    DateTime.UtcNow, delegate { return true; }, out firstSnapshot) &&
                coordinator.TrySwitch(AgentKind.Codex, settings,
                    DateTime.UtcNow, delegate { return true; }, out firstSnapshot) &&
                probe.CodexCreateCount == 1 && probe.DeepSeekHarnessCreateCount == 1 &&
                probe.PeakActiveCount == 1 && codex.SubscriberCount == 1 &&
                deepSeekHarness.SubscriberCount == 0,
                "repeated round-trip reuses providers without overlapping subscriptions");
            coordinator.Dispose();
            Assert(probe.ActiveCount == 0 && codex.DisposeCount == 1 &&
                deepSeekHarness.DisposeCount == 1,
                "coordinator disposes all cached providers once");
        }

        private static void RunAgentSwitchFailureChecks()
        {
            AssertSwitchFailureRollback("candidate start",
                delegate(SwitchingFakeAgentProvider codex,
                    SwitchingFakeAgentProvider deepSeekHarness)
                {
                    deepSeekHarness.StartFailuresRemaining = 1;
                }, delegate { return true; });
            AssertSwitchFailureRollback("candidate read",
                delegate(SwitchingFakeAgentProvider codex,
                    SwitchingFakeAgentProvider deepSeekHarness)
                {
                    deepSeekHarness.ReadFailuresRemaining = 1;
                }, delegate { return true; });
            AssertSwitchFailureRollback("focus persistence",
                delegate(SwitchingFakeAgentProvider codex,
                    SwitchingFakeAgentProvider deepSeekHarness)
                {
                }, delegate { return false; });
            AssertSwitchFailureRollback("old stop after deactivate",
                delegate(SwitchingFakeAgentProvider codex,
                    SwitchingFakeAgentProvider deepSeekHarness)
                {
                    codex.StopFailuresAfterDeactivateRemaining = 1;
                }, delegate { return true; });
        }

        private static void AssertSwitchFailureRollback(string scenario,
            Action<SwitchingFakeAgentProvider,
                SwitchingFakeAgentProvider> configure,
            Func<bool> persist)
        {
            SwitchProbe probe = new SwitchProbe();
            SwitchingFakeAgentProvider codex;
            SwitchingFakeAgentProvider deepSeekHarness;
            AgentProviderCatalog catalog = CreateSwitchCatalog(probe,
                out codex, out deepSeekHarness);
            HaloSettings settings = EnabledSwitchSettings(catalog);
            AgentMonitorCoordinator coordinator =
                new AgentMonitorCoordinator(catalog, AgentKind.Codex);
            int focusChanges = 0;
            coordinator.Changed += delegate(object sender,
                AgentCoordinatorChangedEventArgs e)
            {
                if (e.FocusChanged)
                {
                    focusChanges++;
                }
            };
            coordinator.Start();
            configure(codex, deepSeekHarness);

            AgentProviderSnapshot ignored;
            bool switched = coordinator.TrySwitch(
                AgentKind.DeepSeekHarness, settings, DateTime.UtcNow,
                persist, out ignored);
            Assert(!switched && coordinator.FocusedKind == AgentKind.Codex &&
                settings.FocusedAgent == "codex" &&
                coordinator.IsStarted && codex.IsActive &&
                !deepSeekHarness.IsActive && probe.ActiveCount == 1 &&
                probe.PeakActiveCount <= 1 && focusChanges == 0 &&
                codex.SubscriberCount == 1 &&
                deepSeekHarness.SubscriberCount == 0 &&
                !String.IsNullOrWhiteSpace(coordinator.LastSwitchError),
                "switch rollback restores Codex after " + scenario);
            coordinator.Dispose();
            Assert(probe.ActiveCount == 0,
                "rollback fixture stops after " + scenario);
        }

        private static void RunAgentPauseChecks()
        {
            SwitchProbe probe = new SwitchProbe();
            SwitchingFakeAgentProvider codex;
            SwitchingFakeAgentProvider deepSeekHarness;
            AgentProviderCatalog catalog = CreateSwitchCatalog(probe,
                out codex, out deepSeekHarness);
            HaloSettings settings = EnabledSwitchSettings(catalog);
            AgentMonitorCoordinator coordinator =
                new AgentMonitorCoordinator(catalog, AgentKind.Codex);
            int changedCount = 0;
            coordinator.Changed += delegate { changedCount++; };
            coordinator.Start();
            Action oldCallback = codex.CaptureChanged();
            coordinator.Stop();
            codex.RaiseChanged();
            oldCallback();
            int persistCount = 0;
            AgentProviderSnapshot snapshot;
            bool switched = coordinator.TrySwitch(AgentKind.DeepSeekHarness,
                settings, DateTime.UtcNow, delegate
                {
                    persistCount++;
                    return true;
                }, out snapshot);
            Assert(!switched && persistCount == 0 &&
                coordinator.FocusedKind == AgentKind.Codex &&
                !codex.IsActive && !deepSeekHarness.IsActive &&
                changedCount == 0,
                "paused coordinator rejects switches and old callbacks");
            coordinator.Start();
            codex.RaiseChanged();
            Assert(changedCount == 1 && codex.IsActive &&
                codex.SubscriberCount == 1,
                "resume starts and subscribes the focused provider once");
            coordinator.Dispose();
        }

        private static void RunAgentDetailsIsolationChecks()
        {
            bool usageCreatedBefore =
                CodexUsageMonitor.InstanceCreatedForDiagnostics;
            AgentProviderSnapshot codex = CreateSwitchSnapshot(
                AgentKind.Codex);
            codex.Aggregate.State = HaloState.Working;
            codex.Aggregate.Label = "EXECUTING";
            codex.Aggregate.Presence = AgentPresenceState.Active;
            codex.Details.Mode = AgentDetailsMode.Quota;
            codex.Details.Usage = new UsageMetrics
            {
                HasFiveHour = true,
                FiveHourUsedPercent = 20,
                ContextInputTokens = 50,
                ContextWindowTokens = 100
            };
            AgentProviderSnapshot deepSeekHarness = CreateSwitchSnapshot(
                AgentKind.DeepSeekHarness);
            deepSeekHarness.Aggregate.State = HaloState.Working;
            deepSeekHarness.Aggregate.Label = "EXECUTING";
            deepSeekHarness.Aggregate.Presence = AgentPresenceState.Active;
            deepSeekHarness.Details.Mode = AgentDetailsMode.DeepSeekTask;
            deepSeekHarness.Details.TaskTitle = "Synthetic DSH task";
            deepSeekHarness.Details.ModelName = "deepseek-v4";
            deepSeekHarness.Details.ModelSourceKey =
                "details.deepseek.model_source.current_turn";

            DetailsWindow panel = new DetailsWindow();
            panel.SetEnabledAgents(new[]
            {
                AgentKind.Codex, AgentKind.DeepSeekHarness
            });
            panel.UpdateContent(codex);
            Assert(panel.SelectedAgentForDiagnostics == AgentKind.Codex &&
                panel.QuotaVisibleForDiagnostics &&
                panel.ContextVisibleForDiagnostics,
                "Codex details display quota and context");
            panel.UpdateContent(deepSeekHarness);
            Assert(panel.SelectedAgentForDiagnostics ==
                    AgentKind.DeepSeekHarness &&
                panel.DeepSeekTaskVisibleForDiagnostics &&
                !panel.QuotaVisibleForDiagnostics &&
                !panel.InformationVisibleForDiagnostics &&
                !panel.ContextVisibleForDiagnostics,
                "DSH task details hide Codex quota, custom API, and context data");
            Assert(panel.DeepSeekTaskTitleForDiagnostics ==
                    "Synthetic DSH task" &&
                panel.DeepSeekModelNameForDiagnostics == "deepseek-v4",
                "DSH panel displays only the task title and main agent model");

            codex.Details.Mode = AgentDetailsMode.Information;
            codex.Details.ProjectName = "AgentHalo";
            codex.Details.ModelName = "codex-custom-model";
            codex.Details.InputTokens = 1400;
            codex.Details.OutputTokens = 80;
            codex.Details.ContextInputTokens = 50;
            codex.Details.ContextWindowTokens = 100;
            panel.UpdateContent(codex);
            Assert(panel.SelectedAgentForDiagnostics == AgentKind.Codex &&
                panel.InformationVisibleForDiagnostics &&
                !panel.QuotaVisibleForDiagnostics &&
                panel.ContextVisibleForDiagnostics,
                "Codex custom API details display separately from quota");
            panel.UpdateContent(deepSeekHarness);
            Assert(panel.DeepSeekTaskVisibleForDiagnostics &&
                !panel.InformationVisibleForDiagnostics &&
                !panel.QuotaVisibleForDiagnostics &&
                !panel.ContextVisibleForDiagnostics &&
                panel.DeepSeekTaskTitleForDiagnostics ==
                    "Synthetic DSH task" &&
                panel.DeepSeekModelNameForDiagnostics == "deepseek-v4",
                "DSH task details do not retain Codex custom API data");
            panel.UpdateContent(codex);
            Assert(panel.SelectedAgentForDiagnostics == AgentKind.Codex &&
                panel.InformationVisibleForDiagnostics &&
                !panel.DeepSeekTaskVisibleForDiagnostics &&
                panel.ContextVisibleForDiagnostics,
                "switching back restores the injected Codex details");
            panel.Close();
            Assert(CodexUsageMonitor.InstanceCreatedForDiagnostics ==
                    usageCreatedBefore,
                "agent detail switching remains synthetic and offline");
        }

        private static AgentProviderCatalog CreateSwitchCatalog(
            SwitchProbe probe, out SwitchingFakeAgentProvider codex,
            out SwitchingFakeAgentProvider deepSeekHarness)
        {
            SwitchingFakeAgentProvider codexProvider =
                new SwitchingFakeAgentProvider(AgentKind.Codex, probe);
            SwitchingFakeAgentProvider deepSeekHarnessProvider =
                new SwitchingFakeAgentProvider(
                    AgentKind.DeepSeekHarness, probe);
            codex = codexProvider;
            deepSeekHarness = deepSeekHarnessProvider;
            return new AgentProviderCatalog(new[]
            {
                new AgentProviderDescriptor
                {
                    Kind = AgentKind.Codex,
                    Key = "codex",
                    DisplayName = "Codex",
                    Order = 0,
                    EnabledByDefault = true,
                    Capabilities = codexProvider.Capabilities,
                    CreateProvider = delegate
                    {
                        probe.CodexCreateCount++;
                        return codexProvider;
                    }
                },
                new AgentProviderDescriptor
                {
                    Kind = AgentKind.DeepSeekHarness,
                    Key = "deepseek-harness",
                    DisplayName = "DeepSeek Harness",
                    Order = 1,
                    EnabledByDefault = false,
                    Capabilities = deepSeekHarnessProvider.Capabilities,
                    CreateProvider = delegate
                    {
                        probe.DeepSeekHarnessCreateCount++;
                        return deepSeekHarnessProvider;
                    }
                }
            });
        }

        private static HaloSettings EnabledSwitchSettings(
            AgentProviderCatalog catalog)
        {
            HaloSettings settings = new HaloSettings
            {
                FocusedAgent = "codex",
                EnabledAgents = new List<string>
                {
                    "codex", "deepseek-harness"
                }
            };
            settings.NormalizeAgentSelection(catalog);
            return settings;
        }

        private static AgentProviderSnapshot CreateSwitchSnapshot(
            AgentKind kind)
        {
            List<SessionSnapshot> sessions = new List<SessionSnapshot>();
            bool isDeepSeekHarness = kind == AgentKind.DeepSeekHarness;
            return new AgentProviderSnapshot
            {
                Aggregate = new AggregateSnapshot
                {
                    State = HaloState.Idle,
                    Label = "OFFLINE",
                    Detail = isDeepSeekHarness
                        ? "DeepSeek Harness offline"
                        : L10n.Instance["status.offline_codex"],
                    Sessions = sessions,
                    FocusedAgent = kind,
                    Presence = AgentPresenceState.Offline,
                    TurnPhase = AgentTurnPhase.None,
                    Activity = AgentActivityKind.None,
                    EvidenceSource = AgentEvidenceSource.None,
                    AttentionReason = AgentAttentionReason.None,
                    FailureSeverity = AgentFailureSeverity.None
                },
                Sessions = sessions,
                Details = new AgentDetailsSnapshot
                {
                    Agent = kind,
                    Mode = isDeepSeekHarness
                        ? AgentDetailsMode.DeepSeekTask
                        : AgentDetailsMode.Quota,
                    TaskTitle = isDeepSeekHarness
                        ? "Synthetic DSH task" : String.Empty,
                    ModelName = isDeepSeekHarness
                        ? "deepseek-v4" : String.Empty,
                    ModelSourceKey = isDeepSeekHarness
                        ? "details.deepseek.model_source.current_turn" :
                            String.Empty,
                    Usage = null,
                    ContextInputTokens = 0,
                    ContextWindowTokens = 0
                },
                Capabilities = isDeepSeekHarness
                    ? AgentCapability.Lifecycle
                    : AgentCapability.Lifecycle |
                      AgentCapability.WindowActivation
            };
        }

        private sealed class SwitchProbe
        {
            public int ActiveCount;
            public int PeakActiveCount;
            public int CodexCreateCount;
            public int DeepSeekHarnessCreateCount;
            public readonly List<string> Sequence = new List<string>();

            public void Activate()
            {
                ActiveCount++;
                PeakActiveCount = Math.Max(PeakActiveCount, ActiveCount);
            }

            public void Deactivate()
            {
                ActiveCount--;
            }
        }

        private sealed class SwitchingFakeAgentProvider : IAgentProvider
        {
            private readonly SwitchProbe probe;
            private EventHandler changed;
            private bool disposed;

            public SwitchingFakeAgentProvider(AgentKind kind,
                SwitchProbe sharedProbe)
            {
                Kind = kind;
                probe = sharedProbe;
                Snapshot = CreateSwitchSnapshot(kind);
                ForegroundResult = true;
                ActivationResult = true;
            }

            public AgentKind Kind { get; private set; }
            public AgentCapability Capabilities
            {
                get
                {
                    return AgentCapability.Lifecycle |
                        AgentCapability.WindowActivation;
                }
            }
            public AgentProviderSnapshot Snapshot { get; private set; }
            public bool IsActive { get; private set; }
            public bool ForegroundResult;
            public bool ActivationResult;
            public bool RaiseChangedOnStop;
            public int StartFailuresRemaining;
            public int ReadFailuresRemaining;
            public int StopFailuresAfterDeactivateRemaining;
            public int StartCount;
            public int StopCount;
            public int RefreshCount;
            public int ReadCount;
            public int ForegroundCount;
            public int ActivateWindowCount;
            public int DisposeCount;
            public int EventAddCount;
            public int EventRemoveCount;
            public int SubscriberCount;

            public event EventHandler Changed
            {
                add
                {
                    changed += value;
                    EventAddCount++;
                    SubscriberCount = changed == null ? 0 :
                        changed.GetInvocationList().Length;
                }
                remove
                {
                    changed -= value;
                    EventRemoveCount++;
                    SubscriberCount = changed == null ? 0 :
                        changed.GetInvocationList().Length;
                }
            }

            public void Start()
            {
                ThrowIfDisposed();
                StartCount++;
                probe.Sequence.Add(Key + ".start");
                if (StartFailuresRemaining > 0)
                {
                    StartFailuresRemaining--;
                    throw new InvalidOperationException(
                        "Synthetic start failure");
                }
                if (!IsActive)
                {
                    IsActive = true;
                    probe.Activate();
                }
            }

            public void Stop()
            {
                StopCount++;
                probe.Sequence.Add(Key + ".stop");
                if (RaiseChangedOnStop)
                {
                    RaiseChanged();
                }
                if (IsActive)
                {
                    IsActive = false;
                    probe.Deactivate();
                }
                if (StopFailuresAfterDeactivateRemaining > 0)
                {
                    StopFailuresAfterDeactivateRemaining--;
                    throw new InvalidOperationException(
                        "Synthetic stop failure after deactivation");
                }
            }

            public bool Refresh()
            {
                ThrowIfDisposed();
                RefreshCount++;
                probe.Sequence.Add(Key + ".refresh");
                return IsActive;
            }

            public AgentProviderSnapshot Read(HaloSettings settings,
                DateTime nowUtc)
            {
                ThrowIfDisposed();
                ReadCount++;
                probe.Sequence.Add(Key + ".read");
                if (ReadFailuresRemaining > 0)
                {
                    ReadFailuresRemaining--;
                    throw new InvalidOperationException(
                        "Synthetic read failure");
                }
                return Snapshot;
            }

            public bool IsForeground(IntPtr foregroundWindow)
            {
                ForegroundCount++;
                return ForegroundResult && foregroundWindow != IntPtr.Zero;
            }

            public bool TryActivateWindow()
            {
                ActivateWindowCount++;
                return ActivationResult;
            }

            public void RaiseChanged()
            {
                EventHandler handler = changed;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }

            public Action CaptureChanged()
            {
                EventHandler captured = changed;
                return delegate
                {
                    if (captured != null)
                    {
                        captured(this, EventArgs.Empty);
                    }
                };
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (IsActive)
                {
                    IsActive = false;
                    probe.Deactivate();
                }
                changed = null;
                SubscriberCount = 0;
                DisposeCount++;
            }

            private string Key
            {
                get
                {
                    return Kind == AgentKind.DeepSeekHarness
                        ? "deepseek-harness" : "codex";
                }
            }

            private void ThrowIfDisposed()
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(
                        "SwitchingFakeAgentProvider");
                }
            }
        }

        private sealed class FakeAgentProvider : IAgentProvider
        {
            public int StartCount;
            public int StopCount;
            public int RefreshCount;
            public int ReadCount;
            public int ForegroundCount;
            public int ActivateWindowCount;
            public int DisposeCount;
            public int StartFailuresRemaining;
            public int StopFailuresRemaining;
            public readonly AgentProviderSnapshot Snapshot;

            public FakeAgentProvider()
            {
                Snapshot = new AgentProviderSnapshot
                {
                    Aggregate = new AggregateSnapshot
                    {
                        State = HaloState.Idle,
                        Label = "OFFLINE",
                        Sessions = new List<SessionSnapshot>(),
                        FocusedAgent = AgentKind.Codex,
                        Presence = AgentPresenceState.Offline
                    },
                    Sessions = new List<SessionSnapshot>(),
                    Details = new AgentDetailsSnapshot
                    {
                        Agent = AgentKind.Codex
                    },
                    Capabilities = Capabilities
                };
            }

            public AgentKind Kind
            {
                get { return AgentKind.Codex; }
            }

            public AgentCapability Capabilities
            {
                get
                {
                    return AgentCapability.Lifecycle |
                        AgentCapability.WindowActivation;
                }
            }

            public event EventHandler Changed;

            public void Start()
            {
                StartCount++;
                if (StartFailuresRemaining > 0)
                {
                    StartFailuresRemaining--;
                    throw new InvalidOperationException("Synthetic start failure");
                }
            }

            public void Stop()
            {
                StopCount++;
                if (StopFailuresRemaining > 0)
                {
                    StopFailuresRemaining--;
                    throw new InvalidOperationException("Synthetic stop failure");
                }
            }

            public bool Refresh()
            {
                RefreshCount++;
                return true;
            }

            public AgentProviderSnapshot Read(HaloSettings settings,
                DateTime nowUtc)
            {
                ReadCount++;
                return Snapshot;
            }

            public bool IsForeground(IntPtr foregroundWindow)
            {
                ForegroundCount++;
                return foregroundWindow != IntPtr.Zero;
            }

            public bool TryActivateWindow()
            {
                ActivateWindowCount++;
                return true;
            }

            public void RaiseChanged()
            {
                EventHandler handler = Changed;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }

            public void Dispose()
            {
                DisposeCount++;
            }
        }


        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + name);
            }
        }


        private static double ColorSaturation(MediaColor color)
        {
            double maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            double minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            return maximum <= 0 ? 0 : (maximum - minimum) / maximum;
        }

        public static int RenderStates(string outputDirectory)
        {
            try
            {
                Assert(!CodexUsageMonitor.InstanceCreatedForDiagnostics,
                    "render states starts without usage singleton");
                Directory.CreateDirectory(outputDirectory);
                HaloState[] states = new HaloState[]
                {
                    HaloState.Idle,
                    HaloState.Thinking,
                    HaloState.Working,
                    HaloState.Done,
                    HaloState.Attention,
                    HaloState.Error
                };
                foreach (HaloState state in states)
                {
                    Grid stage = new Grid();
                    stage.Width = 160;
                    stage.Height = 160;
                    stage.Background = System.Windows.Media.Brushes.Transparent;
                    HaloVisual visual = new HaloVisual();
                    visual.Width = 132;
                    visual.Height = 132;
                    visual.HorizontalAlignment = HorizontalAlignment.Center;
                    visual.VerticalAlignment = VerticalAlignment.Center;
                    visual.SetState(state);
                    visual.SetTestTime(PreviewTimeForState(state));
                    stage.Children.Add(visual);
                    stage.Measure(new System.Windows.Size(160, 160));
                    stage.Arrange(new Rect(0, 0, 160, 160));
                    stage.UpdateLayout();

                    RenderTargetBitmap bitmap = new RenderTargetBitmap(320, 320, 192, 192,
                        PixelFormats.Pbgra32);
                    bitmap.Render(stage);
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string path = Path.Combine(outputDirectory,
                        state.ToString().ToLowerInvariant() + ".png");
                    using (FileStream stream = File.Create(path))
                    {
                        encoder.Save(stream);
                    }
                }
                RenderPanelPreview(outputDirectory);
                RenderMenuPreview(outputDirectory);
                RenderRingBackdropPreview(outputDirectory);
                RenderPeakBrightnessComparison(outputDirectory);
                RenderSizePresetComparison(outputDirectory);
                RenderGapMotionStrip(outputDirectory, HaloState.Idle,
                    "motion-idle.png");
                RenderGapMotionStrip(outputDirectory, HaloState.Working,
                    "motion-working.png");
                RenderGapMotionStrip(outputDirectory, HaloState.Done,
                    "motion-done.png");
                RenderGlowPulseStrip(outputDirectory, HaloState.Thinking,
                    "glow-thinking.png", 5.5);
                RenderGlowPulseStrip(outputDirectory, HaloState.Working,
                    "glow-working.png", 7.2);
                RenderGlowPulseStrip(outputDirectory, HaloState.Attention,
                    "glow-attention-double-pulse.png", 3.35);
                RenderTransitionStrip(outputDirectory, HaloState.Thinking,
                    HaloState.Working, "transition-thinking-working.png");
                RenderTransitionStrip(outputDirectory, HaloState.Working,
                    HaloState.Done, "transition-working-done.png");
                RenderTransitionStrip(outputDirectory, HaloState.Error,
                    HaloState.Thinking, "transition-error-thinking.png");
                RenderSteadyGreenToThinkingStrip(outputDirectory);
                RenderSteadyGreenTransitionStrip(outputDirectory);
                RenderErrorPresentationStrip(outputDirectory,
                    ErrorPresentation.Flashing, ErrorPresentation.Bright,
                    "transition-error-flashing-bright.png");
                RenderErrorPresentationStrip(outputDirectory,
                    ErrorPresentation.Bright, ErrorPresentation.Dim,
                    "transition-error-bright-dim.png");
                RenderErrorPresentationStrip(outputDirectory,
                    ErrorPresentation.Dim, ErrorPresentation.Flashing,
                    "transition-error-dim-flashing.png");
                RenderCompletionFlashStrip(outputDirectory);
                Assert(!CodexUsageMonitor.InstanceCreatedForDiagnostics,
                    "render states does not create usage singleton");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outputDirectory, "render-error.txt"),
                    ex.ToString(), Encoding.UTF8);
                return 1;
            }
        }

        private static void RenderPanelPreview(string outputDirectory)
        {
            DateTime now = DateTime.UtcNow;
            List<SessionSnapshot> sessions = new List<SessionSnapshot>();
            sessions.Add(new SessionSnapshot
            {
                ThreadId = "preview-working",
                ProjectName = "pet-pet",
                WorkingDirectory = @"C:\work\pet-pet",
                State = HaloState.Working,
                Action = "Editing files",
                Active = true,
                LastEventUtc = now
            });
            sessions.Add(new SessionSnapshot
            {
                ThreadId = "preview-thinking",
                ProjectName = "portfolio",
                WorkingDirectory = @"C:\work\portfolio",
                State = HaloState.Thinking,
                Action = "Reviewing result",
                Active = true,
                LastEventUtc = now.AddSeconds(-12)
            });
            sessions.Add(new SessionSnapshot
            {
                ThreadId = "preview-done",
                ProjectName = "api-server",
                WorkingDirectory = @"C:\work\api-server",
                State = HaloState.Done,
                Action = "Complete",
                Active = false,
                LastEventUtc = now.AddMinutes(-4),
                CompletedUtc = now.AddMinutes(-4)
            });
            AggregateSnapshot aggregate = new AggregateSnapshot
            {
                State = HaloState.Working,
                Label = "EXECUTING",
                Detail = "pet-pet +2",
                Sessions = sessions
            };

            AgentProviderSnapshot quotaSnapshot = new AgentProviderSnapshot
            {
                Aggregate = aggregate,
                Sessions = sessions,
                Details = new AgentDetailsSnapshot
                {
                    Agent = AgentKind.Codex,
                    Mode = AgentDetailsMode.Quota,
                    Usage = new UsageMetrics
                    {
                        HasFiveHour = true,
                        FiveHourUsedPercent = 21,
                        FiveHourResetUtc = DateTime.Today.AddHours(14)
                            .AddMinutes(58).ToUniversalTime(),
                        HasWeekly = true,
                        WeeklyUsedPercent = 27,
                        WeeklyResetUtc = DateTime.Today.AddDays(3).AddHours(9)
                            .AddMinutes(36).ToUniversalTime(),
                        ContextInputTokens = 202600,
                        ContextWindowTokens = 258400
                    }
                },
                Capabilities = AgentCapability.Lifecycle |
                    AgentCapability.ToolName |
                    AgentCapability.Attention |
                    AgentCapability.Usage |
                    AgentCapability.ContextWindow |
                    AgentCapability.WindowActivation
            };

            DetailsWindow panel = new DetailsWindow();
            panel.UpdateContent(quotaSnapshot);
            FrameworkElement panelContent = panel.Content as FrameworkElement;
            panel.Content = null;

            double previewContentWidth = panel.Width + 4;
            double previewStageWidth = previewContentWidth + 56;
            Grid stage = new Grid();
            stage.Width = previewStageWidth;
            stage.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            panelContent.Width = previewContentWidth;
            panelContent.Margin = new Thickness(28);
            stage.Children.Add(panelContent);
            stage.Measure(new System.Windows.Size(previewStageWidth, 1000));
            double height = Math.Ceiling(stage.DesiredSize.Height);
            stage.Height = height;
            stage.Arrange(new Rect(0, 0, previewStageWidth, height));
            stage.UpdateLayout();

            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(previewStageWidth * 2), (int)(height * 2),
                192, 192, PixelFormats.Pbgra32);
            bitmap.Render(stage);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory, "panel.png")))
            {
                encoder.Save(stream);
            }
            panel.Close();

            AgentProviderSnapshot customCodexSnapshot = new AgentProviderSnapshot
            {
                Aggregate = aggregate,
                Sessions = sessions,
                Details = new AgentDetailsSnapshot
                {
                    Agent = AgentKind.Codex,
                    Mode = AgentDetailsMode.Information,
                    ProjectName = "AgentHalo",
                    ModelName = "glm-5.2",
                    InputTokens = 14200,
                    OutputTokens = 730,
                    ContextInputTokens = 14200,
                    ContextWindowTokens = 128000
                },
                Capabilities = AgentCapability.Lifecycle |
                    AgentCapability.ToolName |
                    AgentCapability.Attention |
                    AgentCapability.Usage |
                    AgentCapability.ContextWindow |
                    AgentCapability.WindowActivation
            };

            DetailsWindow customCodexPanel = new DetailsWindow();
            customCodexPanel.UpdateContent(customCodexSnapshot);
            FrameworkElement customCodexContent =
                customCodexPanel.Content as FrameworkElement;
            customCodexPanel.Content = null;
            Grid customCodexStage = new Grid();
            customCodexStage.Width = previewStageWidth;
            customCodexStage.Background = new SolidColorBrush(
                MediaColor.FromRgb(7, 10, 15));
            customCodexContent.Width = previewContentWidth;
            customCodexContent.Margin = new Thickness(28);
            customCodexStage.Children.Add(customCodexContent);
            customCodexStage.Measure(new System.Windows.Size(previewStageWidth, 1000));
            double customCodexHeight = Math.Ceiling(
                customCodexStage.DesiredSize.Height);
            customCodexStage.Height = customCodexHeight;
            customCodexStage.Arrange(new Rect(0, 0, previewStageWidth, customCodexHeight));
            customCodexStage.UpdateLayout();
            RenderTargetBitmap customCodexBitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(previewStageWidth * 2),
                (int)(customCodexHeight * 2), 192, 192, PixelFormats.Pbgra32);
            customCodexBitmap.Render(customCodexStage);
            PngBitmapEncoder customCodexEncoder = new PngBitmapEncoder();
            customCodexEncoder.Frames.Add(BitmapFrame.Create(customCodexBitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "panel-codex-custom.png")))
            {
                customCodexEncoder.Save(stream);
            }
            customCodexPanel.Close();

            AgentProviderSnapshot deepSeekTaskSnapshot =
                CreateSwitchSnapshot(AgentKind.DeepSeekHarness);
            deepSeekTaskSnapshot.Aggregate.State = HaloState.Working;
            deepSeekTaskSnapshot.Aggregate.Label = "EXECUTING";
            deepSeekTaskSnapshot.Aggregate.Presence =
                AgentPresenceState.Active;
            deepSeekTaskSnapshot.Details.Mode =
                AgentDetailsMode.DeepSeekTask;
            deepSeekTaskSnapshot.Details.TaskTitle = "Review DSH task state";
            deepSeekTaskSnapshot.Details.ModelName = "deepseek-v4";
            deepSeekTaskSnapshot.Details.ModelSourceKey =
                "details.deepseek.model_source.current_turn";
            RenderAgentPanel(outputDirectory,
                "panel-deepseek-harness-task.png", deepSeekTaskSnapshot);
        }

        private static void RenderAgentPanel(string outputDirectory,
            string fileName, AgentProviderSnapshot snapshot)
        {
            DetailsWindow panel = new DetailsWindow();
            panel.SetEnabledAgents(new[]
            {
                AgentKind.Codex, AgentKind.DeepSeekHarness
            });
            panel.UpdateContent(snapshot);
            FrameworkElement panelContent = panel.Content as FrameworkElement;
            panel.Content = null;

            double previewContentWidth = panel.Width + 4;
            double previewStageWidth = previewContentWidth + 56;
            Grid stage = new Grid();
            stage.Width = previewStageWidth;
            stage.Background = new SolidColorBrush(
                MediaColor.FromRgb(7, 10, 15));
            panelContent.Width = previewContentWidth;
            panelContent.Margin = new Thickness(28);
            stage.Children.Add(panelContent);
            stage.Measure(new System.Windows.Size(previewStageWidth, 1000));
            double height = Math.Ceiling(stage.DesiredSize.Height);
            stage.Height = height;
            stage.Arrange(new Rect(0, 0, previewStageWidth, height));
            stage.UpdateLayout();

            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(previewStageWidth * 2),
                (int)Math.Ceiling(height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(stage);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(
                outputDirectory, fileName)))
            {
                encoder.Save(stream);
            }
            panel.Close();
        }

        private static void RenderMenuPreview(string outputDirectory)
        {
            using (Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip())
            {
                Forms.ToolStripMenuItem topmost =
                    new Forms.ToolStripMenuItem("始终置顶");
                topmost.Checked = true;
                menu.Items.Add(topmost);
                menu.Items.Add("开机自动启动");
                menu.Items.Add("暂停状态监听");
                Forms.ToolStripMenuItem currentAgent =
                    new Forms.ToolStripMenuItem("当前 Agent");
                Forms.ToolStripMenuItem currentCodex =
                    new Forms.ToolStripMenuItem("Codex");
                currentCodex.AccessibleRole =
                    Forms.AccessibleRole.RadioButton;
                currentCodex.Checked = true;
                currentAgent.DropDownItems.Add(currentCodex);
                Forms.ToolStripMenuItem currentDeepSeekHarness =
                    new Forms.ToolStripMenuItem("DeepSeek Harness");
                currentDeepSeekHarness.AccessibleRole =
                    Forms.AccessibleRole.RadioButton;
                currentAgent.DropDownItems.Add(currentDeepSeekHarness);
                menu.Items.Add(currentAgent);
                Forms.ToolStripMenuItem size =
                    new Forms.ToolStripMenuItem("光环大小");
                size.DropDownItems.Add("75%");
                Forms.ToolStripMenuItem current =
                    new Forms.ToolStripMenuItem("100%");
                current.Checked = true;
                size.DropDownItems.Add(current);
                size.DropDownItems.Add("125%");
                menu.Items.Add(size);
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("退出");
                Win11MenuRenderer.Apply(menu);
                menu.CreateControl();
                System.Drawing.Size preferred = menu.GetPreferredSize(
                    new System.Drawing.Size(250, 0));
                menu.Size = preferred;
                menu.PerformLayout();
                using (Bitmap menuBitmap = new Bitmap(menu.Width, menu.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    menu.DrawToBitmap(menuBitmap,
                        new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                    using (Bitmap stage = new Bitmap(menu.Width + 48, menu.Height + 48,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(stage))
                    {
                        graphics.Clear(DrawingColor.FromArgb(239, 242, 244));
                        graphics.DrawImageUnscaled(menuBitmap, 24, 24);
                        stage.Save(Path.Combine(outputDirectory, "menu-win11.png"),
                            System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
        }

        private static void RenderGapMotionStrip(string outputDirectory,
            HaloState state, string fileName)
        {
            double[] times;
            if (state == HaloState.Idle)
            {
                times = new double[] { 0, 1.4, 2.8, 3.7, 4.4, 5.1, 5.8 };
            }
            else if (state == HaloState.Done)
            {
                times = new double[] { 0, 1.1, 2.2, 3.0, 3.8, 4.6, 5.4 };
            }
            else
            {
                times = new double[] { 0, 0.55, 1.1, 1.5, 1.9, 2.25, 2.65 };
            }
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * times.Length;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(21, 24, 28));
            for (int i = 0; i < times.Length; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetState(state);
                visual.SetTestTime(times[i]);
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }

            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory, fileName)))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderGlowPulseStrip(string outputDirectory,
            HaloState state, string fileName, double period)
        {
            const int frameCount = 9;
            const double cellSize = 140;
            Grid strip = new Grid();
            strip.Width = cellSize * frameCount;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(15, 18, 22));
            for (int i = 0; i < frameCount; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 122;
                visual.Height = 122;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetState(state);
                visual.SetTestTime(period * i / (frameCount - 1.0));
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }

            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                fileName)))
            {
                encoder.Save(stream);
            }
        }

        private static double PreviewTimeForState(HaloState state)
        {
            if (state == HaloState.Done)
            {
                return 0.55;
            }
            if (state == HaloState.Thinking)
            {
                return 2.6;
            }
            if (state == HaloState.Working)
            {
                return 1.6;
            }
            return 2.4;
        }

        private static void RenderRingBackdropPreview(string outputDirectory)
        {
            HaloState[] states = new HaloState[]
            {
                HaloState.Thinking,
                HaloState.Working,
                HaloState.Done,
                HaloState.Error
            };
            Grid stage = new Grid();
            stage.Width = 640;
            stage.Height = 320;
            stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
            stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(160) });
            for (int i = 0; i < states.Length; i++)
            {
                stage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            }

            for (int row = 0; row < 2; row++)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    Border cell = new Border();
                    cell.Background = new SolidColorBrush(row == 0
                        ? MediaColor.FromRgb(21, 24, 28)
                        : MediaColor.FromRgb(226, 230, 232));
                    HaloVisual visual = new HaloVisual();
                    visual.Width = 132;
                    visual.Height = 132;
                    visual.HorizontalAlignment = HorizontalAlignment.Center;
                    visual.VerticalAlignment = VerticalAlignment.Center;
                    visual.SetState(states[i]);
                    visual.SetTestTime(PreviewTimeForState(states[i]));
                    cell.Child = visual;
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, i);
                    stage.Children.Add(cell);
                }
            }

            stage.Measure(new System.Windows.Size(stage.Width, stage.Height));
            stage.Arrange(new Rect(0, 0, stage.Width, stage.Height));
            stage.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(1280, 640, 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(stage);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(
                Path.Combine(outputDirectory, "ring-backdrops.png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderPeakBrightnessComparison(string outputDirectory)
        {
            HaloState[] states =
            {
                HaloState.Thinking, HaloState.Working, HaloState.Done, HaloState.Error
            };
            const double cellSize = 170;
            Grid strip = new Grid();
            strip.Width = cellSize * states.Length;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < states.Length; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 136;
                visual.Height = 136;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetState(states[i]);
                if (states[i] == HaloState.Error)
                {
                    visual.SetErrorPresentation(ErrorPresentation.Bright);
                }
                visual.SetTestTime(0.8);
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }
            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "peak-brightness-comparison.png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderSizePresetComparison(string outputDirectory)
        {
            int[] percents = { 75, 100, 125 };
            const double cellSize = 190;
            Grid strip = new Grid();
            strip.Width = cellSize * percents.Length;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < percents.Length; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                double size = HaloWindow.DiagnosticSizeForScale(percents[i]);
                visual.Width = size;
                visual.Height = size;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetState(HaloState.Working);
                visual.SetTestTime(0.8);
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }
            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "size-presets.png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderTransitionStrip(string outputDirectory,
            HaloState from, HaloState to, string fileName)
        {
            const int frameCount = 7;
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * frameCount;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));

            for (int i = 0; i < frameCount; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                double progress = i / (double)(frameCount - 1);
                visual.SetTestTransition(from, to, progress, 2.4 + i * 0.13);
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }

            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory, fileName)))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderSteadyGreenTransitionStrip(string outputDirectory)
        {
            const int frameCount = 9;
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * frameCount;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < frameCount; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetTestSteadyGreenTransition(i / (double)(frameCount - 1));
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }
            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "transition-done-standby.png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderSteadyGreenToThinkingStrip(string outputDirectory)
        {
            const int frameCount = 9;
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * frameCount;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < frameCount; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetTestSteadyGreenToThinking(i / (double)(frameCount - 1));
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }
            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "transition-standby-thinking.png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderErrorPresentationStrip(string outputDirectory,
            ErrorPresentation from, ErrorPresentation to, string fileName)
        {
            const int frameCount = 9;
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * frameCount;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < frameCount; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetTestErrorPresentationTransition(from, to,
                    i / (double)(frameCount - 1));
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }
            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                fileName)))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderCompletionFlashStrip(string outputDirectory)
        {
            double[] times = new double[] { 0, 0.12, 0.18, 0.31, 0.49, 0.56, 0.78, 1.2 };
            const double cellSize = 150;
            Grid strip = new Grid();
            strip.Width = cellSize * times.Length;
            strip.Height = cellSize;
            strip.Background = new SolidColorBrush(MediaColor.FromRgb(7, 10, 15));
            for (int i = 0; i < times.Length; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(cellSize)
                });
                HaloVisual visual = new HaloVisual();
                visual.Width = 126;
                visual.Height = 126;
                visual.HorizontalAlignment = HorizontalAlignment.Center;
                visual.VerticalAlignment = VerticalAlignment.Center;
                visual.SetState(HaloState.Done);
                visual.SetTestTime(times[i]);
                Grid.SetColumn(visual, i);
                strip.Children.Add(visual);
            }

            strip.Measure(new System.Windows.Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(strip.Width * 2), (int)(strip.Height * 2), 192, 192,
                PixelFormats.Pbgra32);
            bitmap.Render(strip);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outputDirectory,
                "completion-double-flash.png")))
            {
                encoder.Save(stream);
            }
        }

        public static void RunBenchmark(string outputPath)
        {
            Window window = new Window();
            window.Width = 112;
            window.Height = 112;
            window.WindowStyle = WindowStyle.None;
            window.AllowsTransparency = true;
            window.Background = System.Windows.Media.Brushes.Transparent;
            window.ShowInTaskbar = false;
            window.Left = SystemParameters.WorkArea.Left + 20;
            window.Top = SystemParameters.WorkArea.Top + 20;
            HaloVisual visual = new HaloVisual();
            visual.SetState(HaloState.Working);
            window.Content = visual;
            window.Show();

            DispatcherTimer measurement = new DispatcherTimer();
            measurement.Interval = TimeSpan.FromSeconds(4);
            measurement.Tick += delegate
            {
                measurement.Stop();
                File.WriteAllText(outputPath,
                    visual.PerformanceSummary, Encoding.UTF8);
                window.Close();
                Application.Current.Shutdown();
            };

            DispatcherTimer warmup = new DispatcherTimer();
            warmup.Interval = TimeSpan.FromSeconds(1);
            warmup.Tick += delegate
            {
                warmup.Stop();
                visual.ResetPerformanceMetrics();
                measurement.Start();
            };
            warmup.Start();
        }
    }
}
