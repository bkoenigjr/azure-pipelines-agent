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
    [DataContract]
    public class JobOutput
    {
        [DataMember]
        public Guid Id { get; set; }

        [DataMember]
        public string Out { get; set; }
    }

    public interface IDaemonPluginContext
    {
        // task info for all steps
        IList<Pipelines.TaskStepDefinitionReference> Steps { get; }

        // all endpoints
        IList<ServiceEndpoint> Endpoints { get; }

        // all repositories
        IList<Pipelines.RepositoryResource> Repositories { get; }

        // all variables
        IDictionary<string, VariableValue> Variables { get; }

        // default SystemConnection back to service use the job oauth token
        VssConnection VssConnection { get; }

        // agent log
        void Trace(string message);

        // user log (job log)
        void Output(string message);

        // user log (job log)
        void Warning(string message);

        // task info for current step
        Pipelines.TaskStepDefinitionReference GetCurrentStep(JobOutput output);
    }

    public interface IAgentDaemonPlugin
    {
        string Name { get; }

        Task ProcessAsync(AgentPluginDaemonContext context, Pipelines.TaskStepDefinitionReference step, IList<string> outputs, CancellationToken token);

        Task FinalizeAsync(AgentPluginDaemonContext context);
    }

    public class AgentPluginDaemonContext
    {
        private VssConnection _connection;

        public List<ServiceEndpoint> Endpoints { get; set; }
        public List<Pipelines.RepositoryResource> Repositories { get; set; }
        public Dictionary<string, VariableValue> Variables { get; set; }
        public Dictionary<Guid, Pipelines.TaskStepDefinitionReference> Steps { get; set; }

        // agent log
        public void Trace(string message)
        {
            Console.WriteLine(message);
        }

        // user log (job log)
        public void Output(string message)
        {
            Console.WriteLine(message);
        }

        // user log (job log)
        public void Warning(string message)
        {
            Console.WriteLine(message);
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


    public class AgentDaemonPluginHost
    {
        private readonly TaskCompletionSource<int> _jobFinished = new TaskCompletionSource<int>();
        private readonly TaskCompletionSource<int> _jobCancelled = new TaskCompletionSource<int>();
        private readonly Dictionary<string, ConcurrentQueue<JobOutput>> _outputQueue = new Dictionary<string, ConcurrentQueue<JobOutput>>();
        private List<IAgentDaemonPlugin> _plugins;
        private AgentPluginDaemonContext _daemonContext;

        public AgentDaemonPluginHost(AgentPluginDaemonContext daemonContext, List<IAgentDaemonPlugin> plugins)
        {
            _daemonContext = daemonContext;
            _plugins = plugins;
            foreach (var plugin in _plugins)
            {
                string typeName = plugin.GetType().FullName;
                _outputQueue[typeName] = new ConcurrentQueue<JobOutput>();
            }
        }

        public async Task Run()
        {
            CancellationTokenSource tokenSource = new CancellationTokenSource();
            List<Task> processTasks = new List<Task>();
            foreach (var plugin in _plugins)
            {
                // start process plugins background
                processTasks.Add(Run(plugin, tokenSource.Token));
            }

            // waiting for job finish event
            await _jobFinished.Task;

            // stop current process tasks
            tokenSource.Cancel();
            await Task.WhenAll(processTasks);

            foreach (var task in processTasks)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {

                }
            }

            // job has finished, all daemon plugins should start their finalize process
            List<Task> finalizeTasks = new List<Task>();
            foreach (var plugin in _plugins)
            {
                var finalize = plugin.FinalizeAsync(_daemonContext);
                finalizeTasks.Add(finalize);
            }

            await Task.WhenAll(finalizeTasks);

            foreach (var task in finalizeTasks)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {

                }

            }
        }

        public void EnqueueConsoleOutput(JobOutput output)
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

        private async Task Run(IAgentDaemonPlugin plugin, CancellationToken token)
        {
            string typeName = plugin.GetType().FullName;
            while (!token.IsCancellationRequested)
            {
                List<Guid> steps = new List<Guid>();
                Dictionary<Guid, List<string>> batch = new Dictionary<Guid, List<string>>();
                while (_outputQueue[typeName].TryDequeue(out JobOutput output))
                {
                    if (!steps.Contains(output.Id))
                    {
                        steps.Add(output.Id);
                        batch[output.Id] = new List<string>();
                    }

                    batch[output.Id].Add(output.Out);
                }
                if (steps.Count > 0)
                {
                    foreach (var step in steps)
                    {
                        try
                        {
                            await plugin.ProcessAsync(_daemonContext, _daemonContext.Steps[step], batch[step], token);
                        }
                        catch (Exception ex)
                        {
                            // trace and ignore exception
                        }
                    }
                }

                try
                {
                    await Task.Delay(10000, token);
                }
                catch (Exception ex)
                {
                    // trace and ignore exception
                }
            }

            if (_outputQueue[typeName].Count() > 0)
            {
                // trace lines didn't get processed.
            }
        }
    }
}
