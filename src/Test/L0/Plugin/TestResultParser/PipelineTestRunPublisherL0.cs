// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Plugins.Log.TestResultParser.Contracts;
using Agent.Plugins.Log.TestResultParser.Plugin;
using Microsoft.TeamFoundation.TestManagement.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.TestResults.WebApi;
using Moq;
using Xunit;
using TestOutcome = Agent.Plugins.Log.TestResultParser.Contracts.TestOutcome;
using TestRun = Agent.Plugins.Log.TestResultParser.Contracts.TestRun;

namespace Test.L0.Plugin.TestResultParser
{
    public class PipelineTestRunPublisherL0
    {
        private PipelineConfig _pipelineConfig;
        public PipelineTestRunPublisherL0 ()
        {
            this._pipelineConfig = new PipelineConfig()
            {
                BuildId = 1,
                Project = new Guid(),
                StageName = "Stage1",
                StageAttempt = 1,
                PhaseName = "Phase1",
                PhaseAttempt = 1,
                JobName = "Job1",
                JobAttempt = 1
            };
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Plugin")]
        public async Task PipelineTestRunPublisher_PublishTestRun()
        {
            var clientFactory = new Mock<IClientFactory>();
            var logger = new Mock<ITraceLogger>();
            var telemetry = new Mock<ITelemetryDataCollector>();
            var testClient = new Mock<TestResultsHttpClient>(new Uri("http://dummyurl"), new VssCredentials());

            clientFactory.Setup(x => x.GetClient<TestResultsHttpClient>()).Returns(testClient.Object);
            testClient.Setup(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()));
            testClient.Setup(x =>
                    x.AddTestResultsToTestRunAsync(It.IsAny<TestCaseResult[]>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new List<TestCaseResult>()));
            testClient.Setup(x =>
                    x.UpdateTestRunAsync(It.IsAny<RunUpdateModel>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()));

            var publisher = new PipelineTestRunPublisher(clientFactory.Object, this._pipelineConfig, logger.Object, telemetry.Object);
            await publisher.PublishAsync(new TestRun("FakeTestResultParser/1", "Fake", 1)
            {
                PassedTests = new List<TestResult>()
                {
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed
                    }
                }
            });

            testClient.Verify(x =>
                x.CreateTestRunAsync(It.Is<RunCreateModel>(run => run.Name.Equals("Fake test run 1 - automatically inferred results", StringComparison.OrdinalIgnoreCase) && ValidatePipelineReference(run)),
                It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.AddTestResultsToTestRunAsync(It.Is<TestCaseResult[]>(res => res.Length == 1),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.UpdateTestRunAsync(It.Is<RunUpdateModel>(run => run.State.Equals("completed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Plugin")]
        public async Task PipelineTestRunPublisher_PublishTestRun_ForBatchedResults()
        {
            var clientFactory = new Mock<IClientFactory>();
            var logger = new Mock<ITraceLogger>();
            var telemetry = new Mock<ITelemetryDataCollector>();
            var testClient = new Mock<TestResultsHttpClient>(new Uri("http://dummyurl"), new VssCredentials());

            clientFactory.Setup(x => x.GetClient<TestResultsHttpClient>()).Returns(testClient.Object);
            testClient.Setup(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()));
            testClient.SetupSequence(x =>
                    x.AddTestResultsToTestRunAsync(It.IsAny<TestCaseResult[]>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new List<TestCaseResult>())).Returns(Task.FromResult(new List<TestCaseResult>()));
            testClient.Setup(x =>
                    x.UpdateTestRunAsync(It.IsAny<RunUpdateModel>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()));

            var publisher = new PipelineTestRunPublisher(clientFactory.Object, this._pipelineConfig, logger.Object, telemetry.Object) { BatchSize = 3 };
            await publisher.PublishAsync(new TestRun("FakeTestResultParser/1", "Fake", 1)
            {
                PassedTests = new List<TestResult>()
                {
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed
                    },
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed
                    },
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed
                    },
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed
                    }
                }
            });

            testClient.Verify(x =>
                x.CreateTestRunAsync(It.Is<RunCreateModel>(run => run.Name.Equals("Fake test run 1 - automatically inferred results", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.UpdateTestRunAsync(It.Is<RunUpdateModel>(run => run.State.Equals("completed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));

            testClient.Verify(x => x.AddTestResultsToTestRunAsync(It.Is<TestCaseResult[]>(res => res.Length == 3),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once);
            testClient.Verify(x => x.AddTestResultsToTestRunAsync(It.Is<TestCaseResult[]>(res => res.Length == 1),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Once);

        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Plugin")]
        public async Task PipelineTestRunPublisher_PublishTestRun_ValidateTestResults()
        {
            var clientFactory = new Mock<IClientFactory>();
            var logger = new Mock<ITraceLogger>();
            var telemetry = new Mock<ITelemetryDataCollector>();
            var testClient = new Mock<TestResultsHttpClient>(new Uri("http://dummyurl"), new VssCredentials());

            clientFactory.Setup(x => x.GetClient<TestResultsHttpClient>()).Returns(testClient.Object);
            testClient.Setup(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()
                {
                    Id = 1
                }));
            testClient.Setup(x =>
                    x.AddTestResultsToTestRunAsync(It.IsAny<TestCaseResult[]>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new List<TestCaseResult>()));
            testClient.Setup(x =>
                    x.UpdateTestRunAsync(It.IsAny<RunUpdateModel>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()
                {
                    Id = 1
                }));

            var publisher = new PipelineTestRunPublisher(clientFactory.Object, this._pipelineConfig, logger.Object, telemetry.Object);
            await publisher.PublishAsync(new TestRun("FakeTestResultParser/1", "Fake", 1)
            {
                PassedTests = new List<TestResult>()
                {
                    new TestResult()
                    {
                        Name = "pass",
                        Outcome = TestOutcome.Passed,
                        ExecutionTime = TimeSpan.FromSeconds(2)
                    }
                },
                FailedTests = new List<TestResult>()
                {
                    new TestResult()
                    {
                        Name = "fail",
                        Outcome = TestOutcome.Failed,
                        StackTrace = "exception",
                        ExecutionTime = TimeSpan.Zero
                    }
                },
                SkippedTests = new List<TestResult>()
                {
                    new TestResult()
                    {
                        Name = "skip",
                        Outcome = TestOutcome.NotExecuted
                    }
                },
            });

            testClient.Verify(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.AddTestResultsToTestRunAsync(It.Is<TestCaseResult[]>(res => res.Length == 3
                                                                                                 && ValidateResult(res[0], TestOutcome.Passed)
                                                                                                 && ValidateResult(res[1], TestOutcome.Failed)
                                                                                                 && ValidateResult(res[2], TestOutcome.NotExecuted)),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.UpdateTestRunAsync(It.Is<RunUpdateModel>(run => run.State.Equals("completed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Plugin")]
        public async Task PipelineTestRunPublisher_PublishTestRun_EmptyTestResults()
        {
            var clientFactory = new Mock<IClientFactory>();
            var logger = new Mock<ITraceLogger>();
            var telemetry = new Mock<ITelemetryDataCollector>();
            var testClient = new Mock<TestResultsHttpClient>(new Uri("http://dummyurl"), new VssCredentials());

            clientFactory.Setup(x => x.GetClient<TestResultsHttpClient>()).Returns(testClient.Object);
            testClient.Setup(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()
                {
                    Id = 1
                }));
            testClient.Setup(x =>
                    x.AddTestResultsToTestRunAsync(It.IsAny<TestCaseResult[]>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Throws<Exception>();
            testClient.Setup(x =>
                    x.UpdateTestRunAsync(It.IsAny<RunUpdateModel>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(new Microsoft.TeamFoundation.TestManagement.WebApi.TestRun()
                {
                    Id = 1
                }));

            var publisher = new PipelineTestRunPublisher(clientFactory.Object, this._pipelineConfig, logger.Object, telemetry.Object);
            await publisher.PublishAsync(new TestRun("FakeTestResultParser/1", "Fake", 1));

            testClient.Verify(x =>
                x.CreateTestRunAsync(It.IsAny<RunCreateModel>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()));
            testClient.Verify(x => x.AddTestResultsToTestRunAsync(It.IsAny<TestCaseResult[]>(), It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()), Times.Never);
            testClient.Verify(x => x.UpdateTestRunAsync(It.Is<RunUpdateModel>(run => run.State.Equals("completed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Guid>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()));
        }

        private bool ValidateResult(TestCaseResult result, TestOutcome outcome)
        {
            switch (outcome)
            {
                case TestOutcome.Passed:
                    return result.AutomatedTestName.Equals("pass") &&
                        result.TestCaseTitle.Equals("pass") &&
                        result.Outcome.Equals("passed", StringComparison.OrdinalIgnoreCase) &&
                        result.DurationInMs == TimeSpan.FromSeconds(2).TotalMilliseconds;
                case TestOutcome.Failed:
                    return result.AutomatedTestName.Equals("fail") &&
                           result.TestCaseTitle.Equals("fail") &&
                           result.Outcome.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                           result.DurationInMs == TimeSpan.FromSeconds(0).TotalMilliseconds &&
                           result.StackTrace.Equals("exception");
                case TestOutcome.NotExecuted:
                    return result.AutomatedTestName.Equals("skip") &&
                           result.TestCaseTitle.Equals("skip") &&
                           result.Outcome.Equals("notexecuted", StringComparison.OrdinalIgnoreCase) &&
                           result.DurationInMs == TimeSpan.FromSeconds(0).TotalMilliseconds;
            }

            return false;
        }

        private bool ValidatePipelineReference(RunCreateModel run)
        {
            bool pipelineId = run.PipelineReference.PipelineId.Equals(1);
            bool stageReference = run.PipelineReference.StageReference.Attempt.Equals(1) && run.PipelineReference.StageReference.StageName.Equals("Stage1");
            bool phaseReference = run.PipelineReference.PhaseReference.Attempt.Equals(1) && run.PipelineReference.PhaseReference.PhaseName.Equals("Phase1");
            bool jobReference = run.PipelineReference.JobReference.Attempt.Equals(1) && run.PipelineReference.JobReference.JobName.Equals("Job1");
            return pipelineId && stageReference && phaseReference && jobReference;
        }
    }
}
