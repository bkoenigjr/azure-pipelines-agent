using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Concurrent;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.TeamFoundation.Framework.Common;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(AgentLogPlugin))]
    public interface IAgentLogPlugin : IAgentService
    {
        Task StartAsync(IExecutionContext jobContext, List<IStep> steps);
        Task WaitAsync(IExecutionContext context);
        void Write(Guid stepId, string message);
    }

    public sealed class AgentLogPlugin : AgentService, IAgentLogPlugin
    {
        private readonly Guid _instanceId = Guid.NewGuid();

        private Task<int> _pluginHostProcess = null;

        private readonly InputQueue<string> _redirectedStdin = new InputQueue<string>();

        private readonly ConcurrentQueue<string> _outputs = new ConcurrentQueue<string>();

        private readonly Dictionary<string, string> _logPlugins = new Dictionary<string, string>()
        {
            { "Agent.Plugins.Log.SampleLogPlugin, Agent.Plugins", "Re-save Log" },
        };

        public Task StartAsync(IExecutionContext jobContext, List<IStep> steps)
        {
            Trace.Entering();
            ArgUtil.NotNull(jobContext, nameof(jobContext));

            // Resolve the working directory.
            string workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

            // Agent.PluginHost
            string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), $"Agent.PluginHost{Util.IOUtil.ExeExtension}");
            ArgUtil.File(file, $"Agent.PluginHost{Util.IOUtil.ExeExtension}");

            // Agent.PluginHost's arguments
            string arguments = $"log \"{_instanceId.ToString("D")}\"";
            foreach (var plugin in _logPlugins)
            {
                arguments = $"{arguments} \"{plugin.Key}\"";
            }

            var processInvoker = HostContext.CreateService<IProcessInvoker>();

            processInvoker.OutputDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _outputs.Enqueue(e.Data);
                }
            };
            processInvoker.ErrorDataReceived += (object sender, ProcessDataReceivedEventArgs e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _outputs.Enqueue(e.Data);
                }
            };

            _pluginHostProcess = processInvoker.ExecuteAsync(workingDirectory: workingDirectory,
                                                         fileName: file,
                                                         arguments: arguments,
                                                         environment: null,
                                                         requireExitCodeZero: true,
                                                         outputEncoding: Encoding.UTF8,
                                                         killProcessOnCancel: true,
                                                         redirectStandardIn: _redirectedStdin,
                                                         cancellationToken: jobContext.CancellationToken);

            // construct plugin context
            AgentLogPluginHostContext pluginContext = new AgentLogPluginHostContext
            {
                Repositories = jobContext.Repositories,
                Endpoints = jobContext.Endpoints,
                Variables = new Dictionary<string, VariableValue>(),
                Steps = new Dictionary<Guid, Pipelines.TaskStepDefinitionReference>()
            };

            // variables
            foreach (var publicVar in jobContext.Variables.Public)
            {
                pluginContext.Variables[publicVar.Key] = publicVar.Value;
            }
            foreach (var publicVar in jobContext.Variables.Private)
            {
                pluginContext.Variables[publicVar.Key] = new VariableValue(publicVar.Value, true);
            }

            // steps
            foreach (var step in steps)
            {
                var taskStep = step as ITaskRunner;
                if (taskStep != null)
                {
                    pluginContext.Steps[taskStep.ExecutionContext.Id] = taskStep.Task.Reference;
                }
            }

            Trace.Info("Send serialized context through STDIN");
            _redirectedStdin.Enqueue(JsonUtility.ToString(pluginContext));

            return Task.CompletedTask;
        }

        public async Task WaitAsync(IExecutionContext context)
        {
            Trace.Entering();
            Trace.Info("Send instruction code through STDIN to stop plugin host");

            // plugin host will stop the routine process and give every plugin a chance to participate into job finalization
            _redirectedStdin.Enqueue($"##vso[logplugin.finish]{_instanceId.ToString("D")}");

            // print out outputs from plugin host and wait for plugin finish
            Trace.Info("Waiting for plugin host exit");
            foreach (var plugin in _logPlugins)
            {
                context.Output($"Waiting for log plugin '{plugin.Value}' to finish.");
            }

            while (!_pluginHostProcess.IsCompleted)
            {
                while (_outputs.TryDequeue(out string output))
                {
                    if (output.StartsWith("##[plugin.trace]", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.Info(output.Substring("##[plugin.trace]".Length));
                    }
                    else
                    {
                        context.Output(output);
                    }
                }

                await Task.WhenAny(Task.Delay(100), _pluginHostProcess);
            }

            // try process output queue again, in case we have buffered outputs haven't process on process exit
            while (_outputs.TryDequeue(out string output))
            {
                if (output.StartsWith("##[plugin.trace]", StringComparison.OrdinalIgnoreCase))
                {
                    Trace.Info(output.Substring("##[plugin.trace]".Length));
                }
                else
                {
                    context.Output(output);
                }
            }

            await _pluginHostProcess;
        }

        public void Write(Guid stepId, string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                _redirectedStdin.Enqueue(JsonUtility.ToString(new JobOutput() { Id = stepId, Out = message }));
            }
        }
    }
}
