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

        public async Task ProcessAsync(IDaemonPluginContext executionContext, IList<JobOutput> outputs, CancellationToken token)
        {
            executionContext.Trace("DEBUG_PROCESS");
            var file = Path.Combine(executionContext.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            executionContext.Output($"Copy... {executionContext.GetCurrentStep(outputs.First()).Name}");
            await File.AppendAllLinesAsync(file, outputs.Select(x => x.Output));
        }

        public async Task FinalizeAsync(IDaemonPluginContext executionContext, IList<JobOutput> outputs, CancellationToken token)
        {
            executionContext.Trace("DEBUG_FINISH");
            var file = Path.Combine(executionContext.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            await File.AppendAllLinesAsync(file, outputs.Select(x => x.Output));
        }
    }
}
