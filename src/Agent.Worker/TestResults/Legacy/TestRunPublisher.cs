// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

﻿using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestRunContext = Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunContext;

namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    [ServiceLocator(Default = typeof(TestRunPublisher))]
    public interface ITestRunPublisher : IAgentService
    {
        void InitializePublisher(IExecutionContext executionContext, VssConnection connection, string projectName, IResultReader resultReader);
        Task<TestRun> StartTestRunAsync(TestRunData testRunData, CancellationToken cancellationToken = default(CancellationToken));
        Task AddResultsAsync(TestRun testRun, TestCaseResultData[] testResults, CancellationToken cancellationToken = default(CancellationToken));
        Task EndTestRunAsync(TestRunData testRunData, int testRunId, bool publishAttachmentsAsArchive = false, CancellationToken cancellationToken = default(CancellationToken));
        TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath, string runName);
        TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath);
    }

    public class TestRunPublisher : AgentService, ITestRunPublisher
    {
        #region Private
        const int BATCH_SIZE = 1000;
        const int PUBLISH_TIMEOUT = 300;
        const int TCM_MAX_FILECONTENT_SIZE = 100 * 1024 * 1024; //100 MB
        const int TCM_MAX_FILESIZE = 75 * 1024 * 1024; // 75 MB
        private IExecutionContext _executionContext;
        private string _projectName;
        private ITestResultsServer _testResultsServer;
        private IResultReader _resultReader;
        #endregion

        #region Public API
        public void InitializePublisher(IExecutionContext executionContext, VssConnection connection, string projectName, IResultReader resultReader)
        {
            Trace.Entering();
            _executionContext = executionContext;
            _projectName = projectName;
            _resultReader = resultReader;
            connection.InnerHandler.Settings.SendTimeout = TimeSpan.FromSeconds(PUBLISH_TIMEOUT);
            _testResultsServer = HostContext.GetService<ITestResultsServer>();
            _testResultsServer.InitializeServer(connection, executionContext);
            Trace.Leaving();
        }

        /// <summary>
        /// Publishes the given results to the test run.
        /// </summary>
        /// <param name="testResults">Results to be published.</param>
        public async Task AddResultsAsync(TestRun testRun, TestCaseResultData[] testResults, CancellationToken cancellationToken)
        {
            Trace.Entering();
            int noOfResultsToBePublished = BATCH_SIZE;

            _executionContext.Output(StringUtil.Loc("PublishingTestResults", testRun.Id));

            for (int i = 0; i < testResults.Length; i += BATCH_SIZE)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i + BATCH_SIZE >= testResults.Length)
                {
                    noOfResultsToBePublished = testResults.Length - i;
                }
                _executionContext.Output(StringUtil.Loc("TestResultsRemaining", (testResults.Length - i), testRun.Id));

                var currentBatch = new TestCaseResultData[noOfResultsToBePublished];
                var testResultsBatch = new TestCaseResult[noOfResultsToBePublished];
                Array.Copy(testResults, i, currentBatch, 0, noOfResultsToBePublished);

                for (int testResultsIndex = 0; testResultsIndex < noOfResultsToBePublished; testResultsIndex++){

                    if (IsMaxLimitReachedForSubresultPreProcessing(currentBatch[testResultsIndex].AutomatedTestName, currentBatch[testResultsIndex].TestCaseSubResultData) == false)
                    {
                        _executionContext.Warning(StringUtil.Loc("MaxHierarchyLevelReached", TestManagementConstants.maxHierarchyLevelForSubresults));
                        currentBatch[testResultsIndex].TestCaseSubResultData = null;
                    }
                    testResultsBatch[testResultsIndex] = new TestCaseResult();
                    TestCaseResultDataConverter.Convert(currentBatch[testResultsIndex], testResultsBatch[testResultsIndex]);
                }

                List<TestCaseResult> uploadedTestResults = await _testResultsServer.AddTestResultsToTestRunAsync(testResultsBatch, _projectName, testRun.Id, cancellationToken);
                for (int j = 0; j < noOfResultsToBePublished; j++)
                {
                    await this.UploadTestResultsAttachmentAsync(testRun.Id, testResults[i + j], uploadedTestResults[j], cancellationToken);
                }
            }

            Trace.Leaving();
        }

        /// <summary>
        /// Start a test run
        /// </summary>
        public async Task<TestRun> StartTestRunAsync(TestRunData testRunData, CancellationToken cancellationToken)
        {
            Trace.Entering();

            var testRun = await _testResultsServer.CreateTestRunAsync(_projectName, testRunData, cancellationToken);
            Trace.Leaving();
            return testRun;
        }

        /// <summary>
        /// Mark the test run as completed
        /// </summary>
        public async Task EndTestRunAsync(TestRunData testRunData, int testRunId, bool publishAttachmentsAsArchive = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            Trace.Entering();
            RunUpdateModel updateModel = new RunUpdateModel(
                completedDate: testRunData.CompleteDate,
                state: TestRunState.Completed.ToString()
                );
            TestRun testRun = await _testResultsServer.UpdateTestRunAsync(_projectName, testRunId, updateModel, cancellationToken);

            // Uploading run level attachments, only after run is marked completed;
            // so as to make sure that any server jobs that acts on the uploaded data (like CoverAn job does for Coverage files)
            // have a fully published test run results, in case it wants to iterate over results

            if (publishAttachmentsAsArchive)
            {
                await UploadTestRunAttachmentsAsArchiveAsync(testRunId, testRunData.Attachments, cancellationToken);
            }
            else
            {
                await UploadTestRunAttachmentsIndividualAsync(testRunId, testRunData.Attachments, cancellationToken);
            }

            _executionContext.Output(string.Format(CultureInfo.CurrentCulture, "Published Test Run : {0}", testRun.WebAccessUrl));
        }

        /// <summary>
        /// Converts the given results file to TestRunData object
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>TestRunData</returns>
        public TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath)
        {
            Trace.Entering();
            return _resultReader.ReadResults(_executionContext, filePath, runContext);
        }

        /// <summary>
        /// Converts the given results file to TestRunData object
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <param name="runName">Run Name</param>
        /// <returns>TestRunData</returns>
        public TestRunData ReadResultsFromFile(TestRunContext runContext, string filePath, string runName)
        {
            Trace.Entering();
            runContext.RunName = runName;
            return _resultReader.ReadResults(_executionContext, filePath, runContext);
        }
        #endregion

        private bool IsMaxLimitReachedForSubresultPreProcessing(string automatedTestName, List<TestCaseSubResultData> subResults, int level = 1)
        {
            int maxSubResultHierarchyLevel = TestManagementConstants.maxHierarchyLevelForSubresults;
            int maxSubResultIterationCount = TestManagementConstants.maxSubResultPerLevel;
            if (subResults == null || subResults.Count == 0)
            {
                return true;
            }
            if (level > maxSubResultHierarchyLevel)
            {
                return false;
            }
            if (subResults.Count > maxSubResultIterationCount)
            {
                _executionContext.Warning(StringUtil.Loc("MaxSubResultLimitReached", automatedTestName, maxSubResultIterationCount));
                subResults.RemoveRange(maxSubResultIterationCount, subResults.Count - maxSubResultIterationCount);
            }
            foreach (var subresult in subResults)
            {
                if (IsMaxLimitReachedForSubresultPreProcessing(automatedTestName, subresult.SubResultData, level + 1) == false)
                {
                    _executionContext.Warning(StringUtil.Loc("MaxHierarchyLevelReached", maxSubResultHierarchyLevel));
                    subresult.SubResultData = null;
                }
            }
            return true;
        }

        private async Task UploadTestResultsAttachmentAsync(int testRunId,
            TestCaseResultData testCaseResultData,
            TestCaseResult testCaseResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (testCaseResult == null || testCaseResultData == null)
            {
                return;
            }

            if (testCaseResultData.AttachmentData != null)
            {
                // Remove duplicate entries
                string[] attachments = testCaseResultData.AttachmentData.AttachmentsFilePathList?.ToArray();
                HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);

                if (attachedFiles != null && attachedFiles.Any())
                {
                    var createAttachmentsTasks = attachedFiles.Select(async attachment =>
                    {
                        TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(attachment);
                        if (reqModel != null)
                        {
                            await _testResultsServer.CreateTestResultAttachmentAsync(reqModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                        }
                    });
                    await Task.WhenAll(createAttachmentsTasks);
                }

                // Upload console log as attachment
                string consoleLog = testCaseResultData?.AttachmentData.ConsoleLog;
                TestAttachmentRequestModel attachmentRequestModel = GetConsoleLogAttachmentRequestModel(consoleLog);
                if (attachmentRequestModel != null)
                {
                    await _testResultsServer.CreateTestResultAttachmentAsync(attachmentRequestModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                }

                // Upload standard error as attachment
                string standardError = testCaseResultData.AttachmentData.StandardError;
                TestAttachmentRequestModel stdErrAttachmentRequestModel = GetStandardErrorAttachmentRequestModel(standardError);
                if (stdErrAttachmentRequestModel != null)
                {
                    await _testResultsServer.CreateTestResultAttachmentAsync(stdErrAttachmentRequestModel, _projectName, testRunId, testCaseResult.Id, cancellationToken);
                }
            }

            if(testCaseResult.SubResults != null && testCaseResult.SubResults.Any() && testCaseResultData.TestCaseSubResultData != null)
            {
                for(int i = 0; i < testCaseResultData.TestCaseSubResultData.Count; i++)
                {
                    await UploadTestSubResultsAttachmentAsync(testRunId, testCaseResult.Id, testCaseResultData.TestCaseSubResultData[i], testCaseResult.SubResults[i], 1, cancellationToken);
                }
            }
        }
        private async Task UploadTestSubResultsAttachmentAsync(int testRunId,
            int testResultId,
            TestCaseSubResultData subResultData,
            TestSubResult subresult,
            int level,
            CancellationToken cancellationToken)
        {
            if (level > TestManagementConstants.maxHierarchyLevelForSubresults || subresult == null || subResultData == null || subResultData.AttachmentData == null)
            {
                return;
            }

            string[] attachments = subResultData.AttachmentData.AttachmentsFilePathList?.ToArray();
            
            // remove duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            if (attachedFiles != null && attachedFiles.Any())
            {
                var createAttachmentsTasks = attachedFiles
                    .Select(async attachment =>
                    {
                        TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(attachment);
                        if(reqModel != null)
                        {
                            await _testResultsServer.CreateTestSubResultAttachmentAsync(reqModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
                        }
                    });
                await Task.WhenAll(createAttachmentsTasks);
            }

            // Upload console log as attachment
            string consoleLog = subResultData.AttachmentData.ConsoleLog;
            TestAttachmentRequestModel attachmentRequestModel = GetConsoleLogAttachmentRequestModel(consoleLog);
            if (attachmentRequestModel != null)
            {
                await _testResultsServer.CreateTestSubResultAttachmentAsync(attachmentRequestModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
            }

            // Upload standard error as attachment
            string standardError = subResultData.AttachmentData.StandardError;
            TestAttachmentRequestModel stdErrAttachmentRequestModel = GetStandardErrorAttachmentRequestModel(standardError);
            if (stdErrAttachmentRequestModel != null)
            {
                await _testResultsServer.CreateTestSubResultAttachmentAsync(stdErrAttachmentRequestModel, _projectName, testRunId, testResultId, subresult.Id, cancellationToken);
            }

            if (subResultData.SubResultData != null)
            {
                for (int i = 0; i < subResultData.SubResultData.Count; ++i)
                {
                    await UploadTestSubResultsAttachmentAsync(testRunId, testResultId, subResultData.SubResultData[i], subresult.SubResults[i], level + 1, cancellationToken);
                }
            }
        }

        private async Task UploadTestRunAttachmentsAsArchiveAsync(int testRunId, string[] attachments, CancellationToken cancellationToken)
        {
            Trace.Entering();
            // Do not upload duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            try
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempDirectory);
                string zipFile = Path.Combine(tempDirectory, "TestResults_" + testRunId + ".zip");

                File.Delete(zipFile); //if there's already file. remove silently without exception
                CreateZipFile(zipFile, attachedFiles);
                await CreateTestRunAttachmentAsync(testRunId, zipFile, cancellationToken);
            }
            catch (Exception ex)
            {
                _executionContext.Warning(StringUtil.Loc("UnableToArchiveResults", ex));
                await UploadTestRunAttachmentsIndividualAsync(testRunId, attachments, cancellationToken);
            }
        }

        private void CreateZipFile(string zipfileName, IEnumerable<string> files)
        {
            Trace.Entering();
            // Create and open a new ZIP file
            using (ZipArchive zip = ZipFile.Open(zipfileName, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    // Add the entry for each file
                    zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                }
            }
        }

        private async Task UploadTestRunAttachmentsIndividualAsync(int testRunId, string[] attachments, CancellationToken cancellationToken)
        {
            Trace.Entering();
            _executionContext.Debug("Uploading test run attachements individually");
            // Do not upload duplicate entries
            HashSet<string> attachedFiles = GetUniqueTestRunFiles(attachments);
            var attachFilesTasks = attachedFiles.Select(async file =>
             {
                 await CreateTestRunAttachmentAsync(testRunId, file, cancellationToken);
             });
            await Task.WhenAll(attachFilesTasks);
        }

        private async Task CreateTestRunAttachmentAsync(int testRunId, string zipFile, CancellationToken cancellationToken)
        {
            Trace.Entering();
            TestAttachmentRequestModel reqModel = GetAttachmentRequestModel(zipFile);
            if (reqModel != null)
            {
                await _testResultsServer.CreateTestRunAttachmentAsync(reqModel, _projectName, testRunId, cancellationToken);
            }
        }

        private string GetAttachmentType(string file)
        {
            Trace.Entering();
            string fileName = Path.GetFileNameWithoutExtension(file);

            if (string.Compare(Path.GetExtension(file), ".coverage", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.CodeCoverage.ToString();
            }
            else if (string.Compare(Path.GetExtension(file), ".trx", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.TmiTestRunSummary.ToString();
            }
            else if (string.Compare(fileName, "testimpact", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.TestImpactDetails.ToString();
            }
            else if (string.Compare(fileName, "SystemInformation", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return AttachmentType.IntermediateCollectorData.ToString();
            }
            else
            {
                return AttachmentType.GeneralAttachment.ToString();
            }
        }

        private TestAttachmentRequestModel GetAttachmentRequestModel(string attachment)
        {
            Trace.Entering();
            if (!File.Exists(attachment))
            {
                _executionContext.Warning(StringUtil.Loc("TestAttachmentNotExists", attachment));
                return null;
            }

            // https://stackoverflow.com/questions/13378815/base64-length-calculation
            if (new FileInfo(attachment).Length <= TCM_MAX_FILESIZE)
            {
                byte[] bytes = File.ReadAllBytes(attachment);
                string encodedData = Convert.ToBase64String(bytes);
                if (encodedData.Length <= TCM_MAX_FILECONTENT_SIZE)
                {
                    // Replace colon character with underscore character as on linux environment, some earlier version of .net core task
                    // were creating trx files with ":" in it, but this is not an acceptable character in Results attachments API
                    string attachmentFileName = Path.GetFileName(attachment);
                    attachmentFileName = attachmentFileName.Replace(":", "_");

                    return new TestAttachmentRequestModel(encodedData, attachmentFileName, "", GetAttachmentType(attachment));
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", attachment));
                }
            }
            else
            {
                _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", attachment));
            }

            return null;
        }

        private TestAttachmentRequestModel GetConsoleLogAttachmentRequestModel(string consoleLog)
        {
            Trace.Entering();
            if (!string.IsNullOrWhiteSpace(consoleLog))
            {
                string consoleLogFileName = "Standard_Console_Output.log";

                if (consoleLog.Length <= TCM_MAX_FILESIZE)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(consoleLog);
                    string encodedData = Convert.ToBase64String(bytes);
                    return new TestAttachmentRequestModel(encodedData, consoleLogFileName, "",
                        AttachmentType.ConsoleLog.ToString());
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", consoleLogFileName));
                }
            }

            return null;
        }

        private TestAttachmentRequestModel GetStandardErrorAttachmentRequestModel(string stdErr)
        {
            Trace.Entering();
            if (string.IsNullOrWhiteSpace(stdErr) == false)
            {
                const string stdErrFileName = "Standard_Console_Error.log";

                if (stdErr.Length <= TCM_MAX_FILESIZE)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(stdErr);
                    string encodedData = Convert.ToBase64String(bytes);
                    return new TestAttachmentRequestModel(encodedData, stdErrFileName, "",
                        AttachmentType.ConsoleLog.ToString());
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("AttachmentExceededMaximum", stdErrFileName));
                }
            }

            return null;
        }

        private HashSet<string> GetUniqueTestRunFiles(string[] attachments)
        {
            var attachedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attachments != null)
            {
                foreach (string attachment in attachments)
                {
                    attachedFiles.Add(attachment);
                }
            }
            return attachedFiles;
        }
    }
}
