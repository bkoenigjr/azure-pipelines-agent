using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Agent.Sdk;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using System.IO;
using Microsoft.VisualStudio.Services.Common;

namespace Agent.Plugins.Logging
{
    public class LoggingPlugin : IAgentDaemonPlugin
    {
        public string Name => "Re-save Log";

        private string _fileName = $"{Guid.NewGuid().ToString("N")}.log";

        public async Task FinalizeAsync(AgentPluginDaemonContext context)
        {
            context.Trace("DEBUG_FINISH");
            var file = Path.Combine(context.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            await File.AppendAllTextAsync(file, StringUtil.ConvertToJson(context));
        }

        public async Task ProcessAsync(AgentPluginDaemonContext context, Pipelines.TaskStepDefinitionReference step, IList<string> outputs, CancellationToken token)
        {
            context.Trace("DEBUG_PROCESS");
            var file = Path.Combine(context.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            context.Output($"Copy... {step.Name}");
            await File.AppendAllLinesAsync(file, outputs);
        }
    }
}
