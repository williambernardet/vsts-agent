using Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public sealed class AgentPluginCommandExtension : AgentService, IWorkerCommandExtension
    {
        public Type ExtensionType => typeof(IWorkerCommandExtension);

        public string CommandArea => "agentplugin";

        public bool Enabled
        {
            get
            {
                return false;
            }
            set
            {
                Enabled = value;
            }
        }

        public HostTypes SupportedHostTypes => HostTypes.Build;

        public void ProcessCommand(IExecutionContext context, Command command)
        {
            if (String.Equals(command.Event, WellKnownAgentPluginCommand.SetRepositoryProperty, StringComparison.OrdinalIgnoreCase))
            {
                ProcessAgentPluginSetRepositoryCommand(context, command.Properties, command.Data);
            }
            else
            {
                throw new Exception(StringUtil.Loc("AgentPluginCommandNotFound", command.Event));
            }
        }


        private void ProcessAgentPluginSetRepositoryCommand(IExecutionContext context, Dictionary<string, string> eventProperties, string data)
        {
            String alias;
            if (!eventProperties.TryGetValue(AgentPluginSetRepositoryEventProperties.Alias, out alias) || String.IsNullOrEmpty(alias))
            {
                throw new Exception(StringUtil.Loc("MissingRepositoryAlias"));
            }

            var repository = context.Repositories.FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (repository == null)
            {
                throw new Exception(StringUtil.Loc("RepositoryNotExist"));
            }

            String ready;
            if (eventProperties.TryGetValue(AgentPluginSetRepositoryEventProperties.Ready, out ready) && String.IsNullOrEmpty(ready))
            {
                repository.Properties.Set("ready", ready);
            }

            String path;
            if (eventProperties.TryGetValue(AgentPluginSetRepositoryEventProperties.Path, out path) && String.IsNullOrEmpty(path))
            {
                repository.Properties.Set(RepositoryPropertyNames.Path, path);
            }
        }
    }

    internal static class WellKnownAgentPluginCommand
    {
        public static readonly String SetRepositoryProperty = "setrepository";
    }

    internal static class AgentPluginSetRepositoryEventProperties
    {
        public static readonly String Alias = "alias";
        public static readonly String Ready = "ready";
        public static readonly String Path = "path";
    }
}