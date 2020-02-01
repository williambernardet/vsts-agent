// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Release.ContainerFetchEngine
{
    public class ContainerFetchEngine : FetchEngine
    {
        public ContainerFetchEngine(
            IContainerProvider containerProvider,
            string rootItemPath,
            string rootDestinationDir)
            : base(containerProvider, rootItemPath, rootDestinationDir)
        {
        }

        public async Task FetchAsync(CancellationToken cancellationToken)
        {
            IEnumerable<ContainerItem> containerItems = await Provider.GetItemsAsync().ConfigureAwait(false);

            await FetchItemsAsync(containerItems, cancellationToken).ConfigureAwait(false);
        }
    }
}
