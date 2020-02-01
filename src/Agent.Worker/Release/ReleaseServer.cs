// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Clients;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts;
using Microsoft.VisualStudio.Services.WebApi;
using RMContracts = Microsoft.VisualStudio.Services.ReleaseManagement.WebApi;

namespace Agent.Worker.Release
{
    public class ReleaseServer
    {
        private VssConnection _connection;
        private Guid _projectId;

        private ReleaseHttpClient _releaseHttpClient { get; }

        public ReleaseServer(VssConnection connection, Guid projectId)
        {
            ArgUtil.NotNull(connection, nameof(connection));

            _connection = connection;
            _projectId = projectId;

            _releaseHttpClient = _connection.GetClient<ReleaseHttpClient>();
        }

        public IEnumerable<AgentArtifactDefinition> GetReleaseArtifactsFromService(int releaseId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var artifacts = _releaseHttpClient.GetAgentArtifactDefinitionsAsync(_projectId, releaseId, cancellationToken: cancellationToken).Result;
            return artifacts;
        }

        public async Task<RMContracts.Release> UpdateReleaseName(
            string releaseId,
            string releaseName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RMContracts.ReleaseUpdateMetadata updateMetadata = new RMContracts.ReleaseUpdateMetadata()
            {
                Name = releaseName,
                Comment = StringUtil.Loc("RMUpdateReleaseNameForReleaseComment", releaseName)
            };
            
            return await _releaseHttpClient.UpdateReleaseResourceAsync(updateMetadata, _projectId, int.Parse(releaseId), cancellationToken: cancellationToken);
        }
    }
}