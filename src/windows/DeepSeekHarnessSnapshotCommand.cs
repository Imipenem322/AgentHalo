using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexHalo
{
internal static class DeepSeekHarnessSnapshotCommand
    {
        internal static int Run(string outputPath)
        {
            try
            {
                using (DeepSeekHarnessAgentProvider provider =
                    new DeepSeekHarnessAgentProvider())
                {
                    provider.Start();
                    provider.Refresh();
                    AgentProviderSnapshot snapshot = provider.Read(
                        new HaloSettings(), DateTime.UtcNow);
                    Dictionary<string, object> result =
                        new Dictionary<string, object>();
                    result["state"] = snapshot.Aggregate.State.ToString();
                    result["label"] = snapshot.Aggregate.Label;
                    result["presence"] = snapshot.Aggregate.Presence.ToString();
                    result["integration"] = snapshot.Integration.State.ToString();
                    result["detail_key"] = snapshot.Details.StatusDetailKey;
                    result["capabilities"] = snapshot.Capabilities.ToString();
                    result["task_count"] = snapshot.Sessions.Count;
                    result["has_task_title"] = !String.IsNullOrWhiteSpace(
                        snapshot.Details.TaskTitle);
                    result["has_main_model"] = snapshot.Details.HasModel;
                    File.WriteAllText(outputPath,
                        new JavaScriptSerializer().Serialize(result),
                        new UTF8Encoding(false));
                    return snapshot.Integration.State ==
                        AgentIntegrationState.Healthy ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DeepSeek snapshot failed: " + ex.Message);
                return 1;
            }
        }
    }
}
