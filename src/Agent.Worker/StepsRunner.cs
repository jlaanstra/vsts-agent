using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    public interface IStep
    {
        // Run even if a previous non-critical step has failed.
        bool AlwaysRun { get; }
        // Treat Failed as SucceededWithIssues.
        bool ContinueOnError { get; }
        // Treat failure as fatal. Subsequent AlwaysRun steps will not run.
        bool Critical { get; }
        string DisplayName { get; }
        bool Enabled { get; }
        IExecutionContext ExecutionContext { get; set; }
        // Always runs. Even if a previous critical step failed.
        bool Finally { get; }
        Task RunAsync();
    }

    [ServiceLocator(Default = typeof(StepsRunner))]
    public interface IStepsRunner : IAgentService
    {
        Task RunAsync(IExecutionContext jobContext, IList<IStep> steps);
    }

    public sealed class StepsRunner : AgentService, IStepsRunner
    {
        public async Task RunAsync(IExecutionContext jobContext, IList<IStep> steps)
        {
            // TaskResult:
            //  Abandoned
            //  Canceled
            //  Failed
            //  Skipped
            //  Succeeded
            //  SucceededWithIssues
            bool stepFailed = false;
            bool criticalStepFailed = false;
            foreach (IStep step in steps)
            {
                Trace.Info($"Processing step: DisplayName='{step.DisplayName}', AlwaysRun={step.AlwaysRun}, ContinueOnError={step.ContinueOnError}, Critical={step.Critical}, Enabled={step.Enabled}, Finally={step.Finally}");

                // TODO: Disabled steps may have already been removed. Investigate.

                // Skip the current step if it is not Enabled.
                if (!step.Enabled
                    // Or if a previous step failed and the current step is not AlwaysRun.
                    || (stepFailed && !step.AlwaysRun && !step.Finally)
                    // Or if a previous Critical step failed and the current step is not Finally.
                    || (criticalStepFailed && !step.Finally))
                {
                    Trace.Info("Skipping step.");
                    step.ExecutionContext.Result = TaskResult.Skipped;
                    continue;
                }

                // Run the step.
                Trace.Info("Starting the step.");
                step.ExecutionContext.Start();
                List<OperationCanceledException> allCancelExceptions = new List<OperationCanceledException>();
                try
                {
                    await step.RunAsync();
                }
                catch (OperationCanceledException ex)
                {
                    // Log the exception and cancel the step.
                    Trace.Error($"Caught cancellation exception from step: {ex}");
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Result = TaskResult.Canceled;
                    //save the OperationCanceledException, merge with OperationCanceledException throw from Async Commands.
                    allCancelExceptions.Add(ex);
                }
                catch (Exception ex)
                {
                    // Log the error and fail the step.
                    Trace.Error($"Caught exception from step: {ex}");
                    step.ExecutionContext.Error(ex);
                    step.ExecutionContext.Result = TaskResult.Failed;
                }

                // Wait till all async commands finish.
                foreach (var command in step.ExecutionContext.AsyncCommands ?? new List<IAsyncCommandContext>())
                {
                    try
                    {
                        // wait async command to finish.
                        await command.WaitAsync();
                    }
                    catch (OperationCanceledException ex)
                    {
                        // log and save the OperationCanceledException, set step result to canceled if the current result is not failed.
                        Trace.Error($"Caught cancellation exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(ex);

                        // if the step already failed, don't set it to canceled.
                        if (step.ExecutionContext.Result != TaskResult.Failed)
                        {
                            step.ExecutionContext.Result = TaskResult.Canceled;
                        }

                        allCancelExceptions.Add(ex);
                    }
                    catch (Exception ex)
                    {
                        // Log the error, set step result to falied if the current result is not canceled.
                        Trace.Error($"Caught exception from async command {command.Name}: {ex}");
                        step.ExecutionContext.Error(ex);

                        // if the step already canceled, don't set it to failed.
                        if (step.ExecutionContext.Result != TaskResult.Canceled)
                        {
                            step.ExecutionContext.Result = TaskResult.Failed;
                        }
                    }
                }

                // TODO: consider use token.IsCancellationRequest determine step cancelled instead of catch OperationCancelException all over the place
                if (step.ExecutionContext.Result == TaskResult.Canceled && allCancelExceptions.Count > 0)
                {
                    step.ExecutionContext.Complete();
                    throw allCancelExceptions.First();
                }

                // Fixup the step result if ContinueOnError.
                if (step.ExecutionContext.Result == TaskResult.Failed && step.ContinueOnError)
                {
                    step.ExecutionContext.Result = TaskResult.SucceededWithIssues;
                    Trace.Info($"Updated step result: {step.ExecutionContext.Result}");
                }
                else
                {
                    Trace.Info($"Step result: {step.ExecutionContext.Result}");
                }

                // Complete the step context.
                step.ExecutionContext.Complete();

                // Update the step failed flags.
                stepFailed = stepFailed || step.ExecutionContext.Result == TaskResult.Failed;
                criticalStepFailed = criticalStepFailed || (step.Critical && step.ExecutionContext.Result == TaskResult.Failed);

                // Update the job result.
                if (step.ExecutionContext.Result == TaskResult.Failed)
                {
                    jobContext.Result = TaskResult.Failed;
                }
                else if ((jobContext.Result ?? TaskResult.Succeeded) == TaskResult.Succeeded &&
                    step.ExecutionContext.Result == TaskResult.SucceededWithIssues)
                {
                    jobContext.Result = TaskResult.SucceededWithIssues;
                }

                Trace.Info($"Current state: job state = '{jobContext.Result}', step failed = {stepFailed}, critical step failed = {criticalStepFailed}");
            }
        }
    }
}
