﻿using System.Configuration;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.ResourceStack.Common.Storage;
using Microsoft.WindowsAzure.ResourceStack.Common.BackgroundJobs;
using Microsoft.AzureArcData.Sample.Jobs.Jobs;
using Microsoft.AzureArcData.Sample.Jobs.JobMetadata;
using Microsoft.AzureArcData.Sample.Common.EventSource;
using Microsoft.AzureArcData.Sample.Common.Constants;

// Get backend env-var
//
Backend backend;
if (!Enum.TryParse(Environment.GetEnvironmentVariable(JobConstants.backendEnvVarName), out backend))
{
    throw new Exception(
        $"Please set the {JobConstants.backendEnvVarName} environment variable to one of the following values: {string.Join(", ", Enum.GetNames(typeof(Backend)))}"
    );
}

// Initiate backend based on the environment variable
//
JobManagementClient jobManagementClient;
switch (backend)
{
    case Backend.cosmosdb:

        jobManagementClient = new JobManagementClient(
            documentServiceEndpoint: new Uri(
                ConfigurationManager.AppSettings["documentServiceEndpoint"]
                    ?? "https://localhost:8081"
            ),
            documentAuthorizationKey: ConfigurationManager.AppSettings["documentAuthorizationKey"]
                ?? "KeyMissing",
            executionAffinity: "global",
            eventSource: new BJSEventSource(),
            encryptionUtility: null
        );

        break;

    case Backend.sqlserver:

        jobManagementClient = new SqlJobManagementClient(
            databaseConnectionString: ConfigurationManager.AppSettings["sqlServerConnectionString"],
            jobDefinitionsTableName: JobConstants.jobTableName,
            executionAffinity: "global",
            eventSource: new BJSEventSource(),
            encryptionUtility: null
        );
        break;

    default:
        throw new Exception($"This demo doesn't have support for {backend} just yet!");
}

JobBuilder newJob;

// 1. Job which is always succeeding and is repeated 5 times
//
newJob = JobBuilder
    .Create(JobConstants.GetJobPartition(), Guid.NewGuid().ToString())
    .WithCallback(typeof(AlwaysSucceedJob))
    .WithMetadata(
        JsonConvert.SerializeObject(new AlwaysSucceedJobMetadata { CallerName = "AzureArcData" })
    )
    .WithRepeatStrategy(5, TimeSpan.FromSeconds(10))
    .WithoutRetryStrategy();

await jobManagementClient.CreateOrUpdateJob(newJob).ConfigureAwait(false);

// 2. Job which is sometimes failing is repeated 5 times and retries failed runs for 3 times
//
newJob = JobBuilder
    .Create(JobConstants.GetJobPartition(), Guid.NewGuid().ToString())
    .WithCallback(typeof(SometimesFailsJob))
    .WithMetadata(
        JsonConvert.SerializeObject(
            new SometimesFailsJobMetadata { CallerName = "AzureArcData", ChanceOfFailure = .25 }
        )
    )
    .WithRepeatStrategy(5, TimeSpan.FromSeconds(5))
    .WithRetryStrategy(3, TimeSpan.FromSeconds(5));

await jobManagementClient.CreateOrUpdateJob(newJob).ConfigureAwait(false);

// 3. Job which is always succeeding, is repeated every day
//
var currentTime = DateTime.UtcNow;
var currentMinute = currentTime.Minute;
var currentHour = currentTime.Hour;

JobRecurrenceSchedule schedule = new JobRecurrenceSchedule()
{
    // Trigger on:
    Minutes = new int[] { currentMinute + 1 }, // +1 minute from the current minute
    Hours = new int[] { currentHour }, // On the current hour
    WeekDays = new DayOfWeek[] { DayOfWeek.Wednesday, DayOfWeek.Thursday } // If it is Wednesday and Thursday
};

newJob = JobBuilder
    .Create(JobConstants.GetJobPartition(), Guid.NewGuid().ToString())
    .WithCallback(typeof(AlwaysSucceedJob))
    .WithMetadata(
        JsonConvert.SerializeObject(new AlwaysSucceedJobMetadata { CallerName = "AzureArcData" })
    )
    .WithRepeatStrategy(
        count: int.MaxValue, // Repeat forever
        interval: 1, // Repeat every 1....
        unit: JobRecurrenceUnit.Day, // ... day
        schedule: schedule // against the schedule defined above
    )
    .WithoutRetryStrategy()
    .WithTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); // On EST

await jobManagementClient.CreateOrUpdateJob(newJob).ConfigureAwait(false);

for (int i = 0; i < 3; i++)
{
    // 4. Job which is using checkpointing
    //
    newJob = JobBuilder
        .Create(JobConstants.GetJobPartition(), Guid.NewGuid().ToString())
        .WithCallback(typeof(CheckpointingJob))
        .WithMetadata(
            JsonConvert.SerializeObject(
                new CheckpointingJobMetadata
                {
                    CallerName = "AzureArcData",
                    CurrentStep = 0,
                    MaxSteps = Random.Shared.Next(5, 20)
                }
            )
        )
        .WithoutRepeatStrategy()
        .WithoutRetryStrategy();

    await jobManagementClient.CreateOrUpdateJob(newJob).ConfigureAwait(false);
}

// 5. Distributed Sequencer Job:
//
//                              +---------------------+
//                              | SometimesFailsJob   |
//                              +---------------------+
//                                        ^
//  +------------------+                  |
//  | AlwaysSucceedJob |-------------------
//  +------------------+                  |
//                                        v
//                              +-----------------------+
//                              | CheckpointingJob      |
//                              +-----------------------+
//
//
// Distributed Sequencers run SometimesFailsJob and CheckpointJob in parallel.
//
// Distributed Sequencers CANNOT be declared using the same SequencerId as they
// fan out and spin up child jobs, which are hard to cancel.
//
var distributedSequencerBuilder = SequencerBuilder
    .Create(
        JobConstants.GetJobPartition(),
        StorageUtility.EscapeStorageKey(Guid.NewGuid().ToString())
    )
    .WithAction(
        "AlwaysSucceedJob",
        typeof(AlwaysSucceedJob).FullName,
        JsonConvert.SerializeObject(new AlwaysSucceedJobMetadata { CallerName = "AzureArcData" })
    )
    .WithAction(
        "SometimesFailsJob",
        typeof(SometimesFailsJob).FullName,
        JsonConvert.SerializeObject(
            new SometimesFailsJobMetadata { CallerName = "AzureArcData", ChanceOfFailure = 0 }
        )
    )
    .WithAction(
        "CheckpointingJob",
        typeof(CheckpointingJob).FullName,
        JsonConvert.SerializeObject(
            new CheckpointingJobMetadata
            {
                CallerName = "AzureArcData",
                CurrentStep = 0,
                MaxSteps = -1
            }
        )
    )
    .WithDependency("AlwaysSucceedJob", "SometimesFailsJob")
    .WithDependency("AlwaysSucceedJob", "CheckpointingJob")
    .WithFlags(SequencerFlags.DeleteSequencerIfCompleted);

await jobManagementClient
    .CreateSequencer(SequencerType.Distributed, distributedSequencerBuilder)
    .ConfigureAwait(false);

// 6. Linear Sequencer Job:
//
//      Exact same as Distributed (including the dependency declaration),
//      except, in linear, only a single action is executed at a time - which
//      allows us to overwrite and cancel - even when it is running.
//
//      I.e. SometimesFailsJob and CheckpointJob runs in Sequencer, respecting
//      their dependency with AlwaysSucceedJob.
//
var linearSequencerBuilder = SequencerBuilder
    .Create(
        JobConstants.GetJobPartition(),
        //
        // Hard-code GUID on purpose, to show we can overwrite
        //
        StorageUtility.EscapeStorageKey("a5082b19-8a6e-4bc5-8fdd-8ef39dfebc39")
    )
    .WithAction(
        "AlwaysSucceedJob",
        typeof(AlwaysSucceedJob).FullName,
        JsonConvert.SerializeObject(new AlwaysSucceedJobMetadata { CallerName = "AzureArcData" }),
        //
        // Configure Action Level settings
        //
        action =>
        {
            action.WithRetryStrategy(
                count: 1,
                interval: TimeSpan.FromSeconds(5),
                mode: JobRecurrenceMode.Linear,
                minInterval: TimeSpan.FromSeconds(3),
                maxInterval: TimeSpan.FromMinutes(1)
            );
            action.WithTimeout(TimeSpan.FromSeconds(10));
        }
    )
    .WithAction(
        "SometimesFailsJob",
        typeof(SometimesFailsJob).FullName,
        JsonConvert.SerializeObject(
            //
            // Fail on purpose
            //
            new SometimesFailsJobMetadata { CallerName = "AzureArcData", ChanceOfFailure = 1 }
        ),
        action =>
        {
            action.WithRetryStrategy(
                count: 3,
                interval: TimeSpan.FromSeconds(2),
                mode: JobRecurrenceMode.Linear,
                minInterval: TimeSpan.FromSeconds(1),
                maxInterval: TimeSpan.FromSeconds(10)
            );
        }
    )
    .WithAction(
        "CheckpointingJob",
        typeof(CheckpointingJob).FullName,
        JsonConvert.SerializeObject(
            new CheckpointingJobMetadata
            {
                CallerName = "AzureArcData",
                CurrentStep = 0,
                MaxSteps = -1
            }
        )
    )
    .WithDependency("AlwaysSucceedJob", "SometimesFailsJob")
    .WithDependency("AlwaysSucceedJob", "CheckpointingJob")
    //
    // Configure Sequencer level settings
    //
    .WithStartTime(DateTime.UtcNow.AddSeconds(1))
    .WithTimeout(TimeSpan.FromMinutes(5))
    .WithRetention(TimeSpan.FromMinutes(60));

// Loop to prove that we can overwrite over and over again
//
for (int i = 0; i < 5; i++)
{
    await jobManagementClient
        .CreateSequencer(SequencerType.Linear, linearSequencerBuilder)
        .ConfigureAwait(false);
}

// Print state
//
while (true)
{
    Console.WriteLine("");
    Console.WriteLine(
        $"----------------------------------------------- {DateTime.UtcNow} | STATUS CHECK ------------------------------------------------"
    );
    Console.WriteLine("");

    var jobs = await jobManagementClient.GetJobs(JobConstants.GetJobPartition());
    jobs.ToList()
        .ForEach(job =>
        {
            bool delete = true;

            // Background Job or Sequencer - could be either
            //
            Console.WriteLine(
                $"JobID: {job.JobId} | CallBack: {job.Callback} | MetaData: {job.Metadata} | Status: {job.State}\n"
                    + $"\tLastExecutionTime: {job.LastExecutionTime} | LastExecutionStatus: {job.LastExecutionStatus} | NextExecutionTime: {job.NextExecutionTime}\n"
                    + $"\tRun: {job.CurrentRepeatCount}/{job.RepeatCount} | Interval: {job.RepeatInterval / 1000}ms"
            );

            // Check if Sequencer
            //
            Task<SequencerAction[]> sequencerActionTasks;
            if (job.SequencerType != SequencerType.NotSpecified)
            {
                sequencerActionTasks = jobManagementClient.GetSequencerActions(
                    sequencerType: job.SequencerType,
                    sequencerPartition: JobConstants.GetJobPartition(),
                    sequencerId: job.JobId
                );

                // Retrieve all Sequencer Actions synchronously
                //
                SequencerAction[]? sequencerActions = sequencerActionTasks.Result;

                // Print Sequencer Action State
                //
                foreach (var action in sequencerActions)
                {
                    Console.WriteLine("");
                    Console.WriteLine($"ActionId: {action.ActionId}");
                    Console.WriteLine($"Result: {action.Result}");
                    Console.WriteLine($"State: {action.State}");
                    Console.WriteLine("");

                    if (
                        action.Result == SequencerActionResult.Failed
                        || action.Result == SequencerActionResult.TimedOut
                        || action.Result == SequencerActionResult.Skipped
                    )
                    {
                        delete = false;
                    }
                }
            }

            if (
                (
                    job.State == JobState.Completed
                    || job.LastExecutionStatus == JobExecutionStatus.Succeeded
                ) && delete
            )
            {
                Console.WriteLine(
                    $"\nDeleting job {job.JobId}, as it's marked with State {job.State} and Last Execution Status: {job.LastExecutionStatus}"
                );
                jobManagementClient.DeleteJob(JobConstants.GetJobPartition(), job.JobId);
            }
        });

    Console.WriteLine("");
    Console.WriteLine(
        "----------------------------------------------------------------------------------------------------------------------------------------"
    );
    Console.WriteLine("");
    Task.Delay(TimeSpan.FromSeconds(1)).Wait();
}
