using System;
using System.ComponentModel;
using System.Globalization;
using Newtonsoft.Json;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class TopLevelTrackingConfig
    {
        [JsonIgnore]
        public DateTimeOffset? LastBuildDirectoryCreatedOn { get; set; }

        [JsonProperty("lastBuildFolderCreatedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastBuildDirectoryCreatedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastBuildDirectoryCreatedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastBuildDirectoryCreatedOn = null;
                    return;
                }

                LastBuildDirectoryCreatedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonProperty("lastBuildFolderNumber")]
        public int LastBuildDirectoryNumber { get; set; }
    }

    public sealed class PipelineTopLevelTrackingConfig
    {
        [JsonIgnore]
        public DateTimeOffset? LastPipelineDirectoryCreatedOn { get; set; }

        [JsonProperty("lastPipelineDirectoryCreatedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastPipelineDirectoryCreatedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastPipelineDirectoryCreatedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastPipelineDirectoryCreatedOn = null;
                    return;
                }

                LastPipelineDirectoryCreatedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonProperty("lastPipelineDirectoryNumber")]
        public int LastPipelineDirectoryNumber { get; set; }
    }
}