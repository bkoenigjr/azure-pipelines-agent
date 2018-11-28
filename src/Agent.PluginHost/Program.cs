using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Agent.Sdk;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Agent.PluginHost
{
    public static class Program
    {
        private static CancellationTokenSource tokenSource = new CancellationTokenSource();
        private static string executingAssemblyLocation = string.Empty;

        public static int Main(string[] args)
        {
            // We can't use the new SocketsHttpHandler for now for both Windows and Linux
            // On linux, Negotiate auth is not working if the TFS url is behind Https
            // On windows, Proxy is not working
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            Console.CancelKeyPress += Console_CancelKeyPress;

            // Set encoding to UTF8, process invoker will use UTF8 write to STDIN
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                string pluginType = args[0];
                if (string.Equals("task", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    ArgUtil.NotNull(args, nameof(args));
                    ArgUtil.Equal(2, args.Length, nameof(args.Length));

                    string assemblyQualifiedName = args[1];
                    ArgUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));

                    AgentTaskPluginExecutionContext executionContext = StringUtil.ConvertFromJson<AgentTaskPluginExecutionContext>(serializedContext);
                    ArgUtil.NotNull(executionContext, nameof(executionContext));

                    VariableValue culture;
                    ArgUtil.NotNull(executionContext.Variables, nameof(executionContext.Variables));
                    if (executionContext.Variables.TryGetValue("system.culture", out culture) &&
                        !string.IsNullOrEmpty(culture?.Value))
                    {
                        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(culture.Value);
                        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture.Value);
                    }

                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    try
                    {
                        Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                        var taskPlugin = Activator.CreateInstance(type) as IAgentTaskPlugin;
                        ArgUtil.NotNull(taskPlugin, nameof(taskPlugin));
                        taskPlugin.RunAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // any exception throw from plugin will fail the task.
                        executionContext.Error(ex.Message);
                        executionContext.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                    }

                    return 0;
                }
                else if (string.Equals("command", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    ArgUtil.NotNull(args, nameof(args));
                    ArgUtil.Equal(2, args.Length, nameof(args.Length));

                    string assemblyQualifiedName = args[1];
                    ArgUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));

                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));

                    AgentCommandPluginExecutionContext executionContext = StringUtil.ConvertFromJson<AgentCommandPluginExecutionContext>(serializedContext);
                    ArgUtil.NotNull(executionContext, nameof(executionContext));

                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    try
                    {
                        Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                        var commandPlugin = Activator.CreateInstance(type) as IAgentCommandPlugin;
                        ArgUtil.NotNull(commandPlugin, nameof(commandPlugin));
                        commandPlugin.ProcessCommandAsync(executionContext, tokenSource.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // any exception throw from plugin will fail the command.
                        executionContext.Error(ex.ToString());
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                    }

                    return 0;
                }
                else if (string.Equals("log", pluginType, StringComparison.OrdinalIgnoreCase))
                {
                    ArgUtil.NotNull(args, nameof(args));

                    // read through commandline arg to get the instance id
                    var instanceId = args.Skip(1).FirstOrDefault();
                    ArgUtil.NotNullOrEmpty(instanceId, nameof(instanceId));

                    // read STDIN, the first line will be the HostContext for the daemon process
                    string serializedContext = Console.ReadLine();
                    ArgUtil.NotNullOrEmpty(serializedContext, nameof(serializedContext));
                    AgentLogPluginHostContext hostContext = StringUtil.ConvertFromJson<AgentLogPluginHostContext>(serializedContext);
                    ArgUtil.NotNull(hostContext, nameof(hostContext));

                    // read through commandline arg to get all plugin assembly name
                    List<IAgentLogPlugin> logPlugins = new List<IAgentLogPlugin>();
                    AssemblyLoadContext.Default.Resolving += ResolveAssembly;
                    try
                    {
                        HashSet<string> plugins = new HashSet<string>();
                        for (int index = 2; index < args.Length; index++)
                        {
                            string assemblyQualifiedName = args[index];
                            ArgUtil.NotNullOrEmpty(assemblyQualifiedName, nameof(assemblyQualifiedName));
                            if (plugins.Add(assemblyQualifiedName))
                            {
                                try
                                {
                                    Type type = Type.GetType(assemblyQualifiedName, throwOnError: true);
                                    var loggingPlugin = Activator.CreateInstance(type) as IAgentLogPlugin;
                                    ArgUtil.NotNull(loggingPlugin, nameof(loggingPlugin));
                                    logPlugins.Add(loggingPlugin);
                                }
                                catch (Exception ex)
                                {
                                    // any exception throw from plugin will get trace and ignore, error from daemon will not fail the job.
                                    hostContext.Trace($"Unable load plugin '{assemblyQualifiedName}':{ex}");
                                }
                            }
                        }
                    }
                    finally
                    {
                        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
                    }

                    // start the daemon process
                    var logPluginHost = new AgentLogPluginHost(hostContext, logPlugins);
                    Task hostTask = logPluginHost.Run();
                    while (true)
                    {
                        var consoleInput = Console.ReadLine();
                        if (string.Equals(consoleInput, $"##vso[logplugin.finish]{instanceId}", StringComparison.OrdinalIgnoreCase))
                        {
                            // singal all plugins, the job has finished.
                            // plugin need to start their finalize process.
                            logPluginHost.Finish();
                            break;
                        }
                        else
                        {
                            JobOutput output = StringUtil.ConvertFromJson<JobOutput>(consoleInput);
                            logPluginHost.EnqueueConsoleOutput(output);
                        }
                    }

                    // wait for the daemon to finish.
                    hostTask.GetAwaiter().GetResult();

                    return 0;
                }
                else
                {
                    throw new ArgumentOutOfRangeException(pluginType);
                }
            }
            catch (Exception ex)
            {
                // infrastructure failure.
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= Console_CancelKeyPress;
            }
        }

        private static Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assembly)
        {
            string assemblyFilename = assembly.Name + ".dll";
            if (string.IsNullOrEmpty(executingAssemblyLocation))
            {
                executingAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            return context.LoadFromAssemblyPath(Path.Combine(executingAssemblyLocation, assemblyFilename));
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            tokenSource.Cancel();
        }
    }
}
