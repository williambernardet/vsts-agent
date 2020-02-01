// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace Microsoft.VisualStudio.Services.Agent.Worker.TestResults.Utils
{
    public class TestResultsConstants
    {
        public static readonly Guid TFSServiceInstanceGuid = new Guid("00025394-6065-48CA-87D9-7F5672854EF7");

        public static readonly Guid TCMServiceInstanceGuid = new Guid("00000054-0000-8888-8000-000000000000");

        public const string UsePublishTestResultsLibFeatureFlag = "TestManagement.Server.UsePublishTestResultsLibInAgent";

        public const string CalculateTestRunSummaryFeatureFlag = "TestManagement.PTR.CalculateTestRunSummary";
    }
}