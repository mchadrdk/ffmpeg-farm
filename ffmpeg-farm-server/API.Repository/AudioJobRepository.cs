﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Transactions;
using API.Service;
using Contract;
using Dapper;

namespace API.Repository
{
    public class AudioJobRepository : JobRepository, IAudioJobRepository
    {
        private readonly string _connectionString;

        public AudioJobRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));

            _connectionString = connectionString;
        }

        public Guid Add(AudioJobRequest request, ICollection<AudioTranscodingJob> jobs)
        {
            Guid jobCorrelationId = Guid.NewGuid();

            using (var scope = new TransactionScope())
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    connection.Execute(
                        "INSERT INTO FfmpegAudioRequest (JobCorrelationId, SourceFilename, DestinationFilename, Needed, Created) VALUES(@JobCorrelationId, @SourceFilename, @DestinationFilename, @Needed, @Created);",
                        new
                        {
                            JobCorrelationId = jobCorrelationId,
                            request.SourceFilename,
                            request.DestinationFilename,
                            request.Needed,
                            Created = DateTime.UtcNow,
                        });

                    foreach (AudioDestinationFormat target in request.Targets)
                    {
                        connection.Execute(
                            "INSERT INTO FfmpegAudioRequestTargets (JobCorrelationId, Codec, Format, Bitrate) VALUES(@JobCorrelationId, @Codec, @Format, @Bitrate);",
                            new
                            {
                                jobCorrelationId,
                                Codec = target.AudioCodec.ToString(),
                                Format = target.Format.ToString(),
                                target.Bitrate
                            });
                    }

                    foreach (AudioTranscodingJob transcodingJob in jobs)
                    {
                        connection.Execute(
                            "INSERT INTO FfmpegAudioJobs (JobCorrelationId, Arguments, Needed, SourceFilename, State) VALUES(@JobCorrelationId, @Arguments, @Needed, @SourceFilename, @State);",
                            new
                            {
                                JobCorrelationId = jobCorrelationId,
                                transcodingJob.Arguments,
                                transcodingJob.Needed,
                                transcodingJob.SourceFilename,
                                transcodingJob.State
                            });
                    }
                }

                scope.Complete();

                return jobCorrelationId;
            }
        }

        public AudioTranscodingJob GetNextTranscodingJob()
        {
            int timeoutSeconds = Convert.ToInt32(ConfigurationManager.AppSettings["TimeoutSeconds"]);
            DateTimeOffset timeout = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(timeoutSeconds));

            using (var connection = Helper.GetConnection())
            {
                connection.Open();

                using (var scope = new TransactionScope())
                {
                    var job = connection.Query<AudioTranscodingJob>(
                            "SELECT TOP 1 Id, Arguments, JobCorrelationId FROM FfmpegAudioJobs WHERE State = @QueuedState OR (State = @InProgressState AND HeartBeat < @Heartbeat) ORDER BY Needed ASC, Id ASC;",
                            new
                            {
                                QueuedState = TranscodingJobState.Queued,
                                InProgressState = TranscodingJobState.InProgress,
                                Heartbeat = timeout
                            })
                        .SingleOrDefault();
                    if (job == null)
                    {
                        return null;
                    }

                    var rowsUpdated =
                        connection.Execute(
                            "UPDATE FfmpegAudioJobs SET State = @State, HeartBeat = @Heartbeat, Started = @Heartbeat WHERE Id = @Id;",
                            new { State = TranscodingJobState.InProgress, Heartbeat = DateTimeOffset.UtcNow, Id = job.Id });
                    if (rowsUpdated == 0)
                    {
                        throw new Exception("Failed to mark row as taken");
                    }

                    scope.Complete();

                    return job;
                }
            }
        }
    }
}