// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.VisualStudio.Services.Agent.Worker.Release
{
    [ServiceLocator(Default = typeof(ReleaseDirectoryManager))]
    public interface IReleaseDirectoryManager : IAgentService
    {
        ReleaseTrackingConfig PrepareArtifactsDirectory(
            string workingDirectory,
            string collectionId,
            string projectId,
            string releaseDefinition);
    }
}