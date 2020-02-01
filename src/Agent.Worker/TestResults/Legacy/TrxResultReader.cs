// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

﻿using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Xml;
using TestRunContext = Microsoft.TeamFoundation.TestClient.PublishTestResults.TestRunContext;

namespace Microsoft.VisualStudio.Services.Agent.Worker.LegacyTestResults
{
    public class TrxResultReader : AgentService, IResultReader
    {
        public Type ExtensionType => typeof(IResultReader);

        private string _attachmentLocation;
        private IdentityRef _runUserIdRef;
        private Dictionary<string, TestCaseDefinition> _definitions;
        private IExecutionContext _executionContext;

        public TrxResultReader()
        {
            AddResultsFileToRunLevelAttachments = true;
        }

        /// <summary>
        /// Reads a trx file from disk, converts it into a TestRunData object.
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <returns>TestRunData</returns>
        public TestRunData ReadResults(IExecutionContext executionContext, string filePath, TestRunContext runContext)
        {
            _executionContext = executionContext;

            _definitions = new Dictionary<string, TestCaseDefinition>();

            List<TestCaseResultData> results = new List<TestCaseResultData>();

            string xmlContents = File.ReadAllText(filePath);
            xmlContents = xmlContents.Replace("xmlns", "ns");

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xmlContents);

            string runName = Name + " Test Run";
            if (runContext != null)
            {
                if (!String.IsNullOrWhiteSpace(runContext.RunName))
                {
                    runName = runContext.RunName;
                }
                else
                {
                    runName = StringUtil.Format("{0} {1} {2}", runName, runContext.Configuration, runContext.Platform);
                }
            }


            string runUser = "";
            if (runContext.Owner != null)
            {
                runUser = runContext.Owner;
            }

            _runUserIdRef = new IdentityRef();
            _runUserIdRef.DisplayName = runUser;

            //Parse the run start and finish times. If either of it is not available, send neither.
            XmlNode node = doc.SelectSingleNode("/TestRun/Times");
            DateTime runStartDate = DateTime.MinValue;
            DateTime runFinishDate = DateTime.MinValue;
            if (node != null && node.Attributes["start"] != null && node.Attributes["finish"] != null)
            {
                if (DateTime.TryParse(node.Attributes["start"].Value, DateTimeFormatInfo.InvariantInfo,DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out runStartDate))
                {
                    _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Setting run start and finish times."));
                    //Only if there is a valid start date.
                    DateTime.TryParse(node.Attributes["finish"].Value, DateTimeFormatInfo.InvariantInfo,DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out runFinishDate);
                    if (runFinishDate < runStartDate)
                    {
                        runFinishDate = runStartDate = DateTime.MinValue;
                        _executionContext.Debug("Run finish date is less than start date. Resetting to min value.");
                    }
                }
            }

            TestRunData testRunData = new TestRunData(
                name: runName,
                buildId: runContext.BuildId,
                startedDate: runStartDate != DateTime.MinValue ? runStartDate.ToString("o") : null,
                completedDate: runFinishDate != DateTime.MinValue ? runFinishDate.ToString("o") : null,
                state: TestRunState.InProgress.ToString(),
                isAutomated: true,
                dueDate: string.Empty,
                type: string.Empty,
                buildFlavor: runContext.Configuration,
                buildPlatform: runContext.Platform,
                releaseUri: runContext.ReleaseUri,
                releaseEnvironmentUri: runContext.ReleaseEnvironmentUri
                );

            //Parse the Deployment node for the runDeploymentRoot - this is the attachment root. Required for .NET Core
            XmlNode deploymentNode = doc.SelectSingleNode("/TestRun/TestSettings/Deployment");
            var _attachmentRoot = string.Empty;
            if (deploymentNode != null && deploymentNode.Attributes["runDeploymentRoot"] != null )
            {
                _attachmentRoot = deploymentNode.Attributes["runDeploymentRoot"].Value;
            }
            else
            {
                _attachmentRoot = Path.GetFileNameWithoutExtension(filePath); // This for platform v1
            }
            _attachmentLocation = Path.Combine(Path.GetDirectoryName(filePath), _attachmentRoot, "In");
            _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Attachment location: {0}", _attachmentLocation));

            AddRunLevelAttachments(filePath, doc, testRunData);

            // Create a dictionary of testcase definitions to be used when iterating over the results 
            Dictionary<string, TestCaseDefinition> definitions = new Dictionary<string, TestCaseDefinition>();
            if (doc.SelectNodes("/TestRun/TestDefinitions").Count > 0)
            {
                foreach (XmlNode definitionNode in doc.SelectNodes("/TestRun/TestDefinitions")[0])
                {
                    IdentityRef owner = null;
                    string priority = null, storage = null;
                    if (definitionNode.Attributes["storage"] != null && definitionNode.Attributes["storage"].Value != null)
                    {
                        storage = Path.GetFileName(definitionNode.Attributes["storage"].Value);
                    }

                    XmlAttribute priorityAttribute = definitionNode.Attributes["priority"];
                    if (priorityAttribute != null)
                    {
                        priority = priorityAttribute.Value;
                    }

                    XmlNode ownerNode = definitionNode.SelectSingleNode("./Owners/Owner");
                    if (ownerNode != null)
                    {
                        IdentityRef ownerIdRef = new IdentityRef();
                        ownerIdRef.DisplayName = ownerNode.Attributes["name"].Value;
                        ownerIdRef.DirectoryAlias = ownerNode.Attributes["name"].Value;
                        owner = ownerIdRef;
                    }

                    // The automated test name should be FQDN, if we are unable to figure it out like in case of a webtest, 
                    // set it as "name" from the parent (where it is always present) 
                    XmlNode testResultNode = definitionNode.SelectSingleNode("./TestMethod");
                    string automatedTestName = null;
                    if (testResultNode != null && testResultNode.Attributes["className"] != null && testResultNode.Attributes["name"] != null)
                    {
                        // At times the class names are coming as 
                        // className="MS.TF.Test.AgileX.VSTests.WiLinking.UI.WiLinkingUIQueryTests"
                        // at other times, they are as 
                        // className="UnitTestProject3.UnitTest1, UnitTestProject3, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" 
                        string className = testResultNode.Attributes["className"].Value.Split(',')[0];
                        automatedTestName = className + "." + testResultNode.Attributes["name"].Value;
                    }
                    else if (definitionNode.Attributes["name"] != null)
                    {
                        automatedTestName = definitionNode.Attributes["name"].Value;
                    }

                    _definitions.Add(definitionNode.Attributes["id"].Value, new TestCaseDefinition(automatedTestName, owner, priority, storage));
                }
            }

            // Read UnitTestResults, WebTestResults and OrderedTestResults
            XmlNodeList resultsNodes = doc.SelectNodes("/TestRun/Results/UnitTestResult");
            XmlNodeList webTestResultNodes = doc.SelectNodes("/TestRun/Results/WebTestResult");
            XmlNodeList orderedTestResultNodes = doc.SelectNodes("/TestRun/Results/TestResultAggregation");

            results.AddRange(ReadActualResults(resultsNodes, TestType.UnitTest));
            results.AddRange(ReadActualResults(webTestResultNodes, TestType.WebTest));
            results.AddRange(ReadActualResults(orderedTestResultNodes, TestType.OrderedTest));


            testRunData.Results = results.ToArray<TestCaseResultData>();
            _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Total test results: {0}", testRunData.Results.Length));

            return testRunData;
        }

        public bool AddResultsFileToRunLevelAttachments
        {
            get;
            set;
        }

        public string Name => "VSTest";

        private void AddRunLevelAttachments(string filePath, XmlDocument doc, TestRunData testRunData)
        {
            var runAttachments = new List<string>();
            if (AddResultsFileToRunLevelAttachments)
            {
                runAttachments.Add(filePath);

                AddDataCollectorFilesAsRunLevelAttachments(doc, runAttachments);
                AddResultFilesAsRunLevelAttachment(doc, runAttachments);
                AddCodeCoverageSourceFilesAsRunLevelAttachment(doc, runAttachments);
            }

            testRunData.Attachments = runAttachments.ToArray();
        }

        private void AddCodeCoverageSourceFilesAsRunLevelAttachment(XmlDocument doc, List<string> runAttachments)
        {
            // Needed for mstest.exe generated result files which are now getting used in the "PublishTestResults" task 
            // And for static Codecoverage(VS2010), binary and pdb files need to be uploaded as well 
            XmlNodeList staticCodeCoverageNodes =
                doc.SelectNodes(
                    "/TestRun/TestSettings/Execution/AgentRule/DataCollectors/DataCollector/Configuration/CodeCoverage/Regular/CodeCoverageItem");
            foreach (XmlNode staticCodeCoverageNode in staticCodeCoverageNodes)
            {
                if (staticCodeCoverageNode.Attributes["binaryFile"] != null &&
                    staticCodeCoverageNode.Attributes["binaryFile"].Value != null)
                {
                    runAttachments.Add(Path.Combine(_attachmentLocation, @"../Out",
                        Path.GetFileName(staticCodeCoverageNode.Attributes["binaryFile"].Value)));
                    _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Adding run level attachment: {0}",
                        runAttachments.Last()));
                }
                if (staticCodeCoverageNode.Attributes["pdbFile"] != null &&
                    staticCodeCoverageNode.Attributes["pdbFile"].Value != null)
                {
                    runAttachments.Add(Path.Combine(_attachmentLocation, @"../Out",
                        Path.GetFileName(staticCodeCoverageNode.Attributes["pdbFile"].Value)));
                    _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Adding run level attachment: {0}",
                        runAttachments.Last()));
                }
            }
        }

        private void AddResultFilesAsRunLevelAttachment(XmlDocument doc, List<string> runAttachments)
        {
            // Needed for static codecoverage and any other run level result files generated at ModuleInit levels 
            XmlNodeList runResultFileNodes = doc.SelectNodes("/TestRun/ResultSummary/ResultFiles/ResultFile");
            foreach (XmlNode runResultFileNode in runResultFileNodes)
            {
                if (runResultFileNode.Attributes["path"] != null && runResultFileNode.Attributes["path"].Value != null)
                {
                    runAttachments.Add(Path.Combine(_attachmentLocation, runResultFileNode.Attributes["path"].Value));
                    _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Adding run level attachment: {0}",
                        runAttachments.Last()));
                }
            }
        }

        private void AddDataCollectorFilesAsRunLevelAttachments(XmlDocument doc, List<string> runAttachments)
        {
            XmlNodeList runAttachmentNodes =
                doc.SelectNodes("/TestRun/ResultSummary/CollectorDataEntries/Collector/UriAttachments/UriAttachment/A");
            foreach (XmlNode runAttachmentNode in runAttachmentNodes)
            {
                if (runAttachmentNode.Attributes["href"] != null && runAttachmentNode.Attributes["href"].Value != null)
                {
                    runAttachments.Add(Path.Combine(_attachmentLocation, runAttachmentNode.Attributes["href"].Value));
                    _executionContext.Debug(string.Format(CultureInfo.InvariantCulture, "Adding run level attachment: {0}",
                        runAttachments.Last()));
                }
            }
        }

        private List<TestCaseResultData> ReadActualResults(XmlNodeList resultsNodes, TestType testType)
        {
            List<TestCaseResultData> results = new List<TestCaseResultData>();

            object sync = new object();
            Parallel.ForEach<XmlNode>(resultsNodes.Cast<XmlNode>(), resultNode =>
            {
                TestCaseResultData resultCreateModel = new TestCaseResultData()
                {
                    Priority = TestManagementConstants.UnspecifiedPriority,  //Priority is int type so if no priority set then its 255.
                };

                //Find and format dates as per TCM requirement.
                TimeSpan duration;
                if (resultNode.Attributes["duration"] != null && resultNode.Attributes["duration"].Value != null)
                {
                    TimeSpan.TryParse(resultNode.Attributes["duration"].Value, CultureInfo.InvariantCulture, out duration);
                }
                else
                {
                    duration = TimeSpan.Zero;
                }
                resultCreateModel.DurationInMs = duration.TotalMilliseconds;

                DateTime startedDate;
                if (resultNode.Attributes["startTime"] != null && resultNode.Attributes["startTime"].Value != null)
                {
                    DateTime.TryParse(resultNode.Attributes["startTime"].Value, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedDate);
                }
                else
                {
                    startedDate = DateTime.UtcNow;
                }
                resultCreateModel.StartedDate = startedDate;

                DateTime completedDate = startedDate.AddTicks(duration.Ticks);
                resultCreateModel.CompletedDate = completedDate;

                if ((DateTime.Compare(default(DateTime), startedDate) < 0 && DateTime.Compare(startedDate, (DateTime) SqlDateTime.MinValue) <= 0)
                    || (DateTime.Compare(default(DateTime), completedDate) < 0 && DateTime.Compare(completedDate, (DateTime) SqlDateTime.MinValue) <= 0)) {
                        
                        DateTime utcNow = DateTime.UtcNow;
                        resultCreateModel.StartedDate = utcNow;
                        resultCreateModel.CompletedDate = utcNow.AddTicks(duration.Ticks);
                }

                if (resultNode.Attributes["outcome"] == null || resultNode.Attributes["outcome"].Value == null || string.Equals(resultNode.Attributes["outcome"].Value, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Failed.ToString(); ;
                }               
                else if (string.Equals(resultNode.Attributes["outcome"].Value, "passed", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Passed.ToString();
                }
                else if (string.Equals(resultNode.Attributes["outcome"].Value, "inconclusive", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Inconclusive.ToString();
                }
                else
                {                    
                    resultCreateModel.Outcome = TestOutcome.NotExecuted.ToString();
                }               

                if (resultNode.Attributes["testName"] != null && resultNode.Attributes["testName"].Value != null)
                {
                    resultCreateModel.TestCaseTitle = resultNode.Attributes["testName"].Value;
                }

                resultCreateModel.State = "Completed";

                resultCreateModel.AutomatedTestType = testType.ToString();

                if (resultNode.Attributes["computerName"] != null && resultNode.Attributes["computerName"].Value != null)
                {
                    resultCreateModel.ComputerName = resultNode.Attributes["computerName"].Value;
                }

                if (resultNode.Attributes["testId"] != null && resultNode.Attributes["testId"].Value != null)
                {
                    resultCreateModel.AutomatedTestId = resultNode.Attributes["testId"].Value;
                }

                string executionId = null;
                if (resultNode.Attributes["executionId"] != null && resultNode.Attributes["executionId"].Value != null)
                {
                    executionId = resultNode.Attributes["executionId"].Value;
                }

                lock (sync)
                {
                    if (resultCreateModel.AutomatedTestId != null && _definitions.ContainsKey(resultCreateModel.AutomatedTestId))
                    {
                        TestCaseDefinition definition = _definitions[resultCreateModel.AutomatedTestId];
                        if (definition != null)
                        {
                            if (definition.Storage != null)
                            {
                                resultCreateModel.AutomatedTestStorage = definition.Storage;
                            }
                            if (definition.Priority != null)
                            {
                                resultCreateModel.Priority = !string.IsNullOrEmpty(definition.Priority) ? Convert.ToInt32(definition.Priority) : TestManagementConstants.UnspecifiedPriority;
                            }
                            if (definition.Owner != null)
                            {
                                resultCreateModel.Owner = definition.Owner;
                            }
                            if (definition.AutomatedTestName != null)
                            {
                                resultCreateModel.AutomatedTestName = definition.AutomatedTestName;
                            }
                        }
                    }

                    //AutomatedTestId should be a valid guid. Delaying the check to here since we use it as dictionary key above.
                    Guid automatedTestId;
                    if (!Guid.TryParse(resultCreateModel.AutomatedTestId, out automatedTestId))
                    {
                        resultCreateModel.AutomatedTestId = null;
                    }
                }

                if (resultNode.Attributes["testType"] != null && resultNode.Attributes["testType"].Value != null)
                {
                    Guid automatedTestType;
                    if (Guid.TryParse(resultNode.Attributes["testType"].Value, out automatedTestType))
                    {
                        resultCreateModel.AutomatedTestTypeId = resultNode.Attributes["testType"].Value;
                    }
                }

                resultCreateModel.RunBy = _runUserIdRef;

                List<string> resulLeveltAttachments = new List<string>() { };

                XmlNodeList resultAttachmentNodes = resultNode.SelectNodes("CollectorDataEntries/Collector/UriAttachments/UriAttachment/A");
                if (resultAttachmentNodes.Count > 0 && executionId != null)
                {
                    foreach (XmlNode resultAttachmentNode in resultAttachmentNodes)
                    {
                        if (resultAttachmentNode.Attributes["href"]?.Value != null)
                        {
                            resulLeveltAttachments.Add(Path.Combine(_attachmentLocation, executionId, resultAttachmentNode.Attributes["href"].Value));
                        }
                    }
                }

                XmlNodeList resultFileNodes = resultNode.SelectNodes("ResultFiles/ResultFile");

                if (resultFileNodes.Count > 0 && executionId != null)
                {
                    foreach (XmlNode resultFileNode in resultFileNodes)
                    {
                        if (resultFileNode.Attributes["path"]?.Value != null)
                        {
                            resulLeveltAttachments.Add(Path.Combine(_attachmentLocation, executionId, resultFileNode.Attributes["path"].Value));
                        }
                    }
                }

                resultCreateModel.AttachmentData = new AttachmentData() { AttachmentsFilePathList = resulLeveltAttachments.ToArray() };

                AddVariousLogsForResultIfOutcomeIsFailed(resultCreateModel, resultNode);

                XmlNode innerResults = resultNode.SelectSingleNode("InnerResults");
                if (innerResults != null)
                {
                    resultCreateModel.ResultGroupType = GetResultGroupType(resultNode, testType);

                    XmlNodeList resNodes = innerResults.SelectNodes("UnitTestResult");
                    XmlNodeList webTestResultNodes = innerResults.SelectNodes("WebTestResult");
                    XmlNodeList orderedTestResultNodes = innerResults.SelectNodes("TestResultAggregation");

                    resultCreateModel.TestCaseSubResultData = new List<TestCaseSubResultData>();

                    resultCreateModel.TestCaseSubResultData.AddRange(ReadActualSubResults(resNodes, TestType.UnitTest, 1));
                    resultCreateModel.TestCaseSubResultData.AddRange(ReadActualSubResults(webTestResultNodes, TestType.WebTest, 1));
                    resultCreateModel.TestCaseSubResultData.AddRange(ReadActualSubResults(orderedTestResultNodes, TestType.OrderedTest, 1));
                }

                lock (sync)
                {
                    //Mandatory fields. Skip if they are not available.
                    if (!string.IsNullOrEmpty(resultCreateModel.AutomatedTestName) && !string.IsNullOrEmpty(resultCreateModel.TestCaseTitle))
                    {
                        results.Add(resultCreateModel);
                    }

                }
            });

            return results;
        }

        private List<TestCaseSubResultData> ReadActualSubResults(XmlNodeList resultsNodes, TestType testType, int level)
        {
            List<TestCaseSubResultData> results = new List<TestCaseSubResultData>();

            if (level > TestManagementConstants.maxHierarchyLevelForSubresults)
            {
                _executionContext.Warning(StringUtil.Loc("MaxHierarchyLevelReached", TestManagementConstants.maxHierarchyLevelForSubresults));
                return results;
            }
            object sync = new object();
            var resultXmlNodes = resultsNodes.Cast<XmlNode>();
            
            foreach (var resultNode in resultXmlNodes)
            {
                TestCaseSubResultData resultCreateModel = new TestCaseSubResultData();

                //Find and format dates as per TCM requirement.
                TimeSpan duration;
                if (resultNode.Attributes["duration"] != null && resultNode.Attributes["duration"].Value != null)
                {
                    TimeSpan.TryParse(resultNode.Attributes["duration"].Value, CultureInfo.InvariantCulture, out duration);
                }
                else
                {
                    duration = TimeSpan.Zero;
                }
                resultCreateModel.DurationInMs = (long)duration.TotalMilliseconds;

                DateTime startedDate;
                if (resultNode.Attributes["startTime"] != null && resultNode.Attributes["startTime"].Value != null)
                {
                    DateTime.TryParse(resultNode.Attributes["startTime"].Value, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedDate);
                }
                else
                {
                    startedDate = DateTime.UtcNow;
                }
                resultCreateModel.StartedDate = startedDate;

                DateTime completedDate = startedDate.AddTicks(duration.Ticks);
                resultCreateModel.CompletedDate = completedDate;

                if (resultNode.Attributes["outcome"] == null || resultNode.Attributes["outcome"].Value == null || string.Equals(resultNode.Attributes["outcome"].Value, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Failed.ToString(); ;
                }               
                else if (string.Equals(resultNode.Attributes["outcome"].Value, "passed", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Passed.ToString();
                }
                else if (string.Equals(resultNode.Attributes["outcome"].Value, "inconclusive", StringComparison.OrdinalIgnoreCase))
                {
                    resultCreateModel.Outcome = TestOutcome.Inconclusive.ToString();
                }
                else
                {                    
                    resultCreateModel.Outcome = TestOutcome.NotExecuted.ToString();
                }               

                if (resultNode.Attributes["testName"] != null && resultNode.Attributes["testName"].Value != null)
                {
                    resultCreateModel.DisplayName = resultNode.Attributes["testName"].Value;
                }

                if (resultNode.Attributes["computerName"] != null && resultNode.Attributes["computerName"].Value != null)
                {
                    resultCreateModel.ComputerName = resultNode.Attributes["computerName"].Value;
                }

                string executionId = null;
                if (resultNode.Attributes["executionId"] != null && resultNode.Attributes["executionId"].Value != null)
                {
                    executionId = resultNode.Attributes["executionId"].Value;
                }

                List<string> resulLeveltAttachments = new List<string>() { };

                XmlNodeList resultAttachmentNodes = resultNode.SelectNodes("CollectorDataEntries/Collector/UriAttachments/UriAttachment/A");
                if (resultAttachmentNodes.Count > 0 && executionId != null)
                {
                    foreach (XmlNode resultAttachmentNode in resultAttachmentNodes)
                    {
                        if (resultAttachmentNode.Attributes["href"] != null && resultAttachmentNode.Attributes["href"].Value != null)
                        {
                            resulLeveltAttachments.Add(Path.Combine(_attachmentLocation, executionId, resultAttachmentNode.Attributes["href"].Value));
                        }
                    }
                }

                XmlNodeList resultFileNodes = resultNode.SelectNodes("ResultFiles/ResultFile");

                if (resultFileNodes.Count > 0 && executionId != null)
                {
                    foreach (XmlNode resultFileNode in resultFileNodes)
                    {
                        if (resultFileNode.Attributes["path"] != null && resultFileNode.Attributes["path"].Value != null)
                        {
                            resulLeveltAttachments.Add(Path.Combine(_attachmentLocation, executionId, resultFileNode.Attributes["path"].Value));
                        }
                    }
                }

                resultCreateModel.AttachmentData = new AttachmentData() { AttachmentsFilePathList = resulLeveltAttachments.ToArray() };

                AddVariousLogsForSubresultIfOutcomeIsFailed(resultCreateModel, resultNode);

                XmlNode innerResults = resultNode.SelectSingleNode("InnerResults");
                if (innerResults != null)
                {
                    resultCreateModel.ResultGroupType = GetResultGroupType(resultNode, testType);

                    XmlNodeList resNodes = innerResults.SelectNodes("UnitTestResult");
                    XmlNodeList webTestResultNodes = innerResults.SelectNodes("WebTestResult");
                    XmlNodeList orderedTestResultNodes = innerResults.SelectNodes("TestResultAggregation");

                    resultCreateModel.SubResultData = new List<TestCaseSubResultData>();

                    resultCreateModel.SubResultData.AddRange(ReadActualSubResults(resNodes, TestType.UnitTest, level+1));
                    resultCreateModel.SubResultData.AddRange(ReadActualSubResults(webTestResultNodes, TestType.WebTest, level + 1));
                    resultCreateModel.SubResultData.AddRange(ReadActualSubResults(orderedTestResultNodes, TestType.OrderedTest, level + 1));
                }

                results.Add(resultCreateModel);
            }

            return results;
        }

        private void AddVariousLogsForResultIfOutcomeIsFailed(TestCaseResultData resultCreateModel, XmlNode resultNode)
        {
            if (!resultCreateModel.Outcome.Equals("Failed")) return;
            XmlNode errorMessage, errorStackTrace, consoleLog, standardError;

            if ((errorMessage = resultNode.SelectSingleNode("./Output/ErrorInfo/Message")) != null
                && !string.IsNullOrWhiteSpace(errorMessage.InnerText))
            {
                resultCreateModel.ErrorMessage = errorMessage.InnerText;
            }

            // stack trace
            if ((errorStackTrace = resultNode.SelectSingleNode("./Output/ErrorInfo/StackTrace")) != null
                && !string.IsNullOrWhiteSpace(errorStackTrace.InnerText))
            {
                resultCreateModel.StackTrace = errorStackTrace.InnerText;
            }

            // console log
            if ((consoleLog = resultNode.SelectSingleNode("./Output/StdOut")) != null
                && !string.IsNullOrWhiteSpace(consoleLog.InnerText))
            {
                resultCreateModel.AttachmentData.ConsoleLog = consoleLog.InnerText;
            }

            // standard error
            if ((standardError = resultNode.SelectSingleNode("./Output/StdErr")) != null
                && !string.IsNullOrWhiteSpace(standardError.InnerText))
            {
                resultCreateModel.AttachmentData.StandardError = standardError.InnerText;
            }
        }

        private void AddVariousLogsForSubresultIfOutcomeIsFailed(TestCaseSubResultData resultCreateModel, XmlNode resultNode)
        {
            if (!resultCreateModel.Outcome.Equals("Failed")) return;
            XmlNode errorMessage, errorStackTrace, consoleLog, standardError;

            if ((errorMessage = resultNode.SelectSingleNode("./Output/ErrorInfo/Message")) != null
                && !string.IsNullOrWhiteSpace(errorMessage.InnerText))
            {
                resultCreateModel.ErrorMessage = errorMessage.InnerText;
            }

            // stack trace
            if ((errorStackTrace = resultNode.SelectSingleNode("./Output/ErrorInfo/StackTrace")) != null
                && !string.IsNullOrWhiteSpace(errorStackTrace.InnerText))
            {
                resultCreateModel.StackTrace = errorStackTrace.InnerText;
            }

            // console log
            if ((consoleLog = resultNode.SelectSingleNode("./Output/StdOut")) != null
                && !string.IsNullOrWhiteSpace(consoleLog.InnerText))
            {
                resultCreateModel.AttachmentData.ConsoleLog = consoleLog.InnerText;
            }

            // standard error
            if ((standardError = resultNode.SelectSingleNode("./Output/StdErr")) != null
                && !string.IsNullOrWhiteSpace(standardError.InnerText))
            {
                resultCreateModel.AttachmentData.StandardError = standardError.InnerText;
            }
        }

        private ResultGroupType GetResultGroupType(XmlNode resultNode, TestType testType)
        {
            if (testType == TestType.OrderedTest)
            {
                return ResultGroupType.OrderedTest;
            } else
            {
                if (resultNode.Attributes["resultType"]?.Value != null && resultNode.Attributes["resultType"]?.Value == "DataDrivenTest")
                {
                    return ResultGroupType.DataDriven;
                } 
            }

            return ResultGroupType.Generic;
        }

        private enum TestType
        {
            UnitTest,
            WebTest,
            OrderedTest
        }
    }

    internal class TestCaseDefinition
    {
        public string AutomatedTestName { get; private set; }
        public string Storage { get; private set; }
        public string Priority { get; private set; }
        public IdentityRef Owner { get; private set; }

        public TestCaseDefinition(string automatedTestName, IdentityRef owner, string priority, string storage)
        {
            AutomatedTestName = automatedTestName;
            Storage = storage;
            Owner = owner;
            Priority = priority;
        }
    }
}
