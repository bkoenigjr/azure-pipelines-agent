using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.Sdk
{
    public interface IAgentLogPlugin
    {
        string FriendlyName { get; }

        Task ProcessLineAsync(IAgentLogPluginContext context, Pipelines.TaskStepDefinitionReference step, string line);

        Task FinalizeAsync(IAgentLogPluginContext context);
    }

    public interface IAgentLogPluginContext
    {
        // default SystemConnection back to service use the job oauth token
        VssConnection VssConnection { get; }

        // task info for all steps
        IList<Pipelines.TaskStepDefinitionReference> Steps { get; }

        // all endpoints
        IList<ServiceEndpoint> Endpoints { get; }

        // all repositories
        IList<Pipelines.RepositoryResource> Repositories { get; }

        // all variables
        IDictionary<string, VariableValue> Variables { get; }

        // agent log
        void Trace(string message);

        // user log (job log)
        void Output(string message);
    }

    public class AgentLogPluginContext : IAgentLogPluginContext
    {
        private string _pluginName;
        private AgentLogPluginHostContext _hostContext;

        // default SystemConnection back to service use the job oauth token
        public VssConnection VssConnection { get; }

        // task info for all steps
        public IList<Pipelines.TaskStepDefinitionReference> Steps { get; }

        // all endpoints
        public IList<ServiceEndpoint> Endpoints { get; }

        // all repositories
        public IList<Pipelines.RepositoryResource> Repositories { get; }

        // all variables
        public IDictionary<string, VariableValue> Variables { get; }

        public AgentLogPluginContext(
            AgentLogPluginHostContext hostContext,
            string pluginNme,
            VssConnection connection,
            IList<Pipelines.TaskStepDefinitionReference> steps,
            IList<ServiceEndpoint> endpoints,
            IList<Pipelines.RepositoryResource> repositories,
            IDictionary<string, VariableValue> variables)
        {
            _hostContext = hostContext;
            _pluginName = pluginNme;
            VssConnection = connection;
            Steps = steps;
            Endpoints = endpoints;
            Repositories = repositories;
            Variables = variables;
        }

        // agent log
        public void Trace(string message)
        {
            _hostContext.Trace(message);
        }

        // user log (job log)
        public void Output(string message)
        {
            _hostContext.Output($"{_pluginName}: {message}");
        }
    }

    public class AgentLogPluginHostContext
    {
        private VssConnection _connection;

        public List<String> PluginAssemblies { get; set; }
        public List<ServiceEndpoint> Endpoints { get; set; }
        public List<Pipelines.RepositoryResource> Repositories { get; set; }
        public Dictionary<string, VariableValue> Variables { get; set; }
        public Dictionary<Guid, Pipelines.TaskStepDefinitionReference> Steps { get; set; }

        // agent log
        public void Trace(string message)
        {
            Console.WriteLine($"##[plugin.trace]{message}");
        }

        // user log (job log)
        public void Output(string message)
        {
            Console.WriteLine(message);
        }

        public IAgentLogPluginContext CreatePluginContext(IAgentLogPlugin plugin)
        {
            return new AgentLogPluginContext(this, plugin.FriendlyName, VssConnection, Steps.Values.ToList(), Endpoints, Repositories, Variables);
        }

        [JsonIgnore]
        public VssConnection VssConnection
        {
            get
            {
                if (_connection == null)
                {
                    _connection = InitializeVssConnection();
                }
                return _connection;
            }
        }

        private VssConnection InitializeVssConnection()
        {
            var headerValues = new List<ProductInfoHeaderValue>();
            headerValues.Add(new ProductInfoHeaderValue($"VstsAgentCore-Plugin", Variables.GetValueOrDefault("agent.version")?.Value ?? "Unknown"));
            headerValues.Add(new ProductInfoHeaderValue($"({RuntimeInformation.OSDescription.Trim()})"));

            if (VssClientHttpRequestSettings.Default.UserAgent != null && VssClientHttpRequestSettings.Default.UserAgent.Count > 0)
            {
                headerValues.AddRange(VssClientHttpRequestSettings.Default.UserAgent);
            }

            VssClientHttpRequestSettings.Default.UserAgent = headerValues;

            var certSetting = GetCertConfiguration();
            if (certSetting != null)
            {
                if (!string.IsNullOrEmpty(certSetting.ClientCertificateArchiveFile))
                {
                    VssClientHttpRequestSettings.Default.ClientCertificateManager = new AgentClientCertificateManager(certSetting.ClientCertificateArchiveFile, certSetting.ClientCertificatePassword);
                }

                if (certSetting.SkipServerCertificateValidation)
                {
                    VssClientHttpRequestSettings.Default.ServerCertificateValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
            }

            var proxySetting = GetProxyConfiguration();
            if (proxySetting != null)
            {
                if (!string.IsNullOrEmpty(proxySetting.ProxyAddress))
                {
                    VssHttpMessageHandler.DefaultWebProxy = new AgentWebProxy(proxySetting.ProxyAddress, proxySetting.ProxyUsername, proxySetting.ProxyPassword, proxySetting.ProxyBypassList);
                }
            }

            ServiceEndpoint systemConnection = this.Endpoints.FirstOrDefault(e => string.Equals(e.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            ArgUtil.NotNull(systemConnection, nameof(systemConnection));
            ArgUtil.NotNull(systemConnection.Url, nameof(systemConnection.Url));

            VssCredentials credentials = VssUtil.GetVssCredential(systemConnection);
            ArgUtil.NotNull(credentials, nameof(credentials));
            return VssUtil.CreateConnection(systemConnection.Url, credentials);
        }

        private AgentCertificateSettings GetCertConfiguration()
        {
            bool skipCertValidation = StringUtil.ConvertToBoolean(this.Variables.GetValueOrDefault("Agent.SkipCertValidation")?.Value);
            string caFile = this.Variables.GetValueOrDefault("Agent.CAInfo")?.Value;
            string clientCertFile = this.Variables.GetValueOrDefault("Agent.ClientCert")?.Value;

            if (!string.IsNullOrEmpty(caFile) || !string.IsNullOrEmpty(clientCertFile) || skipCertValidation)
            {
                var certConfig = new AgentCertificateSettings();
                certConfig.SkipServerCertificateValidation = skipCertValidation;
                certConfig.CACertificateFile = caFile;

                if (!string.IsNullOrEmpty(clientCertFile))
                {
                    certConfig.ClientCertificateFile = clientCertFile;
                    string clientCertKey = this.Variables.GetValueOrDefault("Agent.ClientCertKey")?.Value;
                    string clientCertArchive = this.Variables.GetValueOrDefault("Agent.ClientCertArchive")?.Value;
                    string clientCertPassword = this.Variables.GetValueOrDefault("Agent.ClientCertPassword")?.Value;

                    certConfig.ClientCertificatePrivateKeyFile = clientCertKey;
                    certConfig.ClientCertificateArchiveFile = clientCertArchive;
                    certConfig.ClientCertificatePassword = clientCertPassword;

                    certConfig.VssClientCertificateManager = new AgentClientCertificateManager(clientCertArchive, clientCertPassword);
                }

                return certConfig;
            }
            else
            {
                return null;
            }
        }

        private AgentWebProxySettings GetProxyConfiguration()
        {
            string proxyUrl = this.Variables.GetValueOrDefault("Agent.ProxyUrl")?.Value;
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                string proxyUsername = this.Variables.GetValueOrDefault("Agent.ProxyUsername")?.Value;
                string proxyPassword = this.Variables.GetValueOrDefault("Agent.ProxyPassword")?.Value;
                List<string> proxyBypassHosts = StringUtil.ConvertFromJson<List<string>>(this.Variables.GetValueOrDefault("Agent.ProxyBypassList")?.Value ?? "[]");
                return new AgentWebProxySettings()
                {
                    ProxyAddress = proxyUrl,
                    ProxyUsername = proxyUsername,
                    ProxyPassword = proxyPassword,
                    ProxyBypassList = proxyBypassHosts,
                    WebProxy = new AgentWebProxy(proxyUrl, proxyUsername, proxyPassword, proxyBypassHosts)
                };
            }
            else
            {
                return null;
            }
        }
    }

    [DataContract]
    public class JobOutput
    {
        [DataMember]
        public Guid Id { get; set; }

        [DataMember]
        public string Out { get; set; }
    }

    public class AgentLogPluginHost
    {
        private readonly TaskCompletionSource<int> _jobFinished = new TaskCompletionSource<int>();
        private readonly Dictionary<string, ConcurrentQueue<JobOutput>> _outputQueue = new Dictionary<string, ConcurrentQueue<JobOutput>>();
        private readonly Dictionary<string, IAgentLogPluginContext> _pluginContexts = new Dictionary<string, IAgentLogPluginContext>();
        private List<IAgentLogPlugin> _plugins;
        private AgentLogPluginHostContext _hostContext;

        public AgentLogPluginHost(AgentLogPluginHostContext hostContext, List<IAgentLogPlugin> plugins)
        {
            _hostContext = hostContext;
            _plugins = plugins;
            foreach (var plugin in _plugins)
            {
                string typeName = plugin.GetType().FullName;
                _outputQueue[typeName] = new ConcurrentQueue<JobOutput>();
                _pluginContexts[typeName] = _hostContext.CreatePluginContext(plugin);
            }
        }

        public async Task Run()
        {
            using (CancellationTokenSource tokenSource = new CancellationTokenSource())
            {
                Dictionary<string, Task> processTasks = new Dictionary<string, Task>();
                foreach (var plugin in _plugins)
                {
                    // start process plugins background
                    _hostContext.Trace($"Start process task for plugin '{plugin.FriendlyName}'");
                    var task = RunAsync(plugin, tokenSource.Token);
                    processTasks[plugin.FriendlyName] = task;
                }

                // waiting for job finish event
                await _jobFinished.Task;
                _hostContext.Trace($"Stop process task for all plugins");

                // stop current process tasks
                tokenSource.Cancel();
                await Task.WhenAll(processTasks.Values);

                foreach (var task in processTasks)
                {
                    try
                    {
                        await task.Value;
                        _hostContext.Trace($"Plugin '{task.Key}' finished log process.");
                    }
                    catch (Exception ex)
                    {
                        _hostContext.Output($"Plugin '{task.Key}' failed with: {ex}");
                    }
                }

                // job has finished, all log plugins should start their finalize process
                Dictionary<string, Task> finalizeTasks = new Dictionary<string, Task>();
                foreach (var plugin in _plugins)
                {
                    _hostContext.Trace($"Start finalize for plugin '{plugin.FriendlyName}'");
                    var finalize = plugin.FinalizeAsync(_pluginContexts[plugin.GetType().FullName]);
                    finalizeTasks[plugin.FriendlyName] = finalize;
                }

                await Task.WhenAll(finalizeTasks.Values);

                foreach (var task in finalizeTasks)
                {
                    try
                    {
                        await task.Value;
                        _hostContext.Trace($"Plugin '{task.Key}' finished job finalize.");
                    }
                    catch (Exception ex)
                    {
                        _hostContext.Output($"Plugin '{task.Key}' failed with: {ex}");
                    }
                }
            }
        }

        public void EnqueueOutput(JobOutput output)
        {
            if (!string.IsNullOrEmpty(output?.Out))
            {
                foreach (var plugin in _plugins)
                {
                    string typeName = plugin.GetType().FullName;
                    _outputQueue[typeName].Enqueue(output);
                }
            }
        }

        public void Finish()
        {
            _jobFinished.TrySetResult(0);
        }

        private async Task RunAsync(IAgentLogPlugin plugin, CancellationToken token)
        {
            List<string> errors = new List<string>();
            string typeName = plugin.GetType().FullName;
            var context = _pluginContexts[typeName];
            using (var registration = token.Register(() => { context.Output($"Pending process {_outputQueue[typeName].Count} log lines."); }))
            {
                while (!token.IsCancellationRequested)
                {
                    while (_outputQueue[typeName].TryDequeue(out JobOutput line))
                    {
                        try
                        {
                            await plugin.ProcessLineAsync(context, _hostContext.Steps[line.Id], line.Out);
                        }
                        catch (Exception ex)
                        {
                            // ignore exception
                            // only trace the first 10 errors.
                            if (errors.Count < 10)
                            {
                                errors.Add(ex.ToString());
                            }
                        }
                    }

                    // back-off before pull output queue again.
                    await Task.Delay(500);
                }
            }

            // process all remaining outputs
            while (_outputQueue[typeName].TryDequeue(out JobOutput line))
            {
                try
                {
                    await plugin.ProcessLineAsync(context, _hostContext.Steps[line.Id], line.Out);
                }
                catch (Exception ex)
                {
                    // ignore exception
                    // only trace the first 10 errors.
                    if (errors.Count < 10)
                    {
                        errors.Add(ex.ToString());
                    }
                }
            }

            // print out error to user.
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    context.Output($"Plugin '{plugin.FriendlyName}' fail to process output: {error}");
                }
            }
        }
    }
}
