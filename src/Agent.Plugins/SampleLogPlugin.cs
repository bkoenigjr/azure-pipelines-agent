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

namespace Agent.Plugins.Log
{
    public class SampleLogPlugin : IAgentLogPlugin
    {
        public string FriendlyName => "Re-save Log";

        private string _fileName = $"{Guid.NewGuid().ToString("N")}.log";

        public async Task FinalizeAsync(IAgentLogPluginContext context)
        {
            context.Trace("DEBUG_FINISH");
            var file = Path.Combine(context.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            await File.AppendAllTextAsync(file, StringUtil.ConvertToJson(context.Variables));
        }

        public async Task ProcessLineAsync(IAgentLogPluginContext context, Pipelines.TaskStepDefinitionReference step, string output)
        {
            context.Trace("DEBUG_PROCESS");
            var file = Path.Combine(context.Variables.GetValueOrDefault("agent.homedirectory").Value, "_diag", _fileName);
            context.Output($"Copy... {step.Name}");
            await File.AppendAllLinesAsync(file, new List<string>() { output });
        }
    }
}
