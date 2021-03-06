﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Contract;
using Newtonsoft.Json.Converters;

namespace ffmpeg_farm_client
{
    internal class Program
    {
        private static readonly System.Timers.Timer TimeSinceLastUpdate = new System.Timers.Timer(TimeSpan.FromSeconds(20).TotalMilliseconds);
        private static TimeSpan _progress = TimeSpan.Zero;
        private static Process _commandlineProcess;
        private static BaseJob _currentJob;
        private static JsonSerializerSettings _jsonSerializerSettings;
        private static StringBuilder _output;

        private static void Main(string[] args)
        {
            if (string.IsNullOrWhiteSpace(ConfigurationManager.AppSettings["FfmpegPath"]))
                throw new ConfigurationErrorsException("No path specified for FFmpeg binary. Missing configuration setting FfmpegPath");
            if (!File.Exists(ConfigurationManager.AppSettings["FfmpegPath"]))
                throw new FileNotFoundException(ConfigurationManager.AppSettings["FfmpegPath"]);

            _jsonSerializerSettings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter>
                                    {
                                        new IsoDateTimeConverter(),
                                        new StringEnumConverter()
                                    },
                TypeNameHandling = TypeNameHandling.All
            };
            _output = new StringBuilder();

            while (true)
            {
                BaseJob receivedJob = null;

                try
                {
                    using (var client = new HttpClient())
                    {
                        HttpResponseMessage result =
                            client.GetAsync(string.Concat(ConfigurationManager.AppSettings["ServerUrl"],
                                "/AudioJob?machinename=" + Environment.MachineName)).Result;
                        if (result.IsSuccessStatusCode)
                        {
                            string json = result.Content.ReadAsStringAsync().Result;
                            if (!string.IsNullOrWhiteSpace(json))
                            {
                                var temp = JsonConvert.DeserializeObject<BaseJob>(json,
                                    _jsonSerializerSettings);
                                switch (temp.Type)
                                {
                                    case JobType.Audio:
                                        receivedJob = JsonConvert.DeserializeObject<AudioTranscodingJob>(json,
                                            _jsonSerializerSettings);
                                        break;
                                    case JobType.Video:
                                        receivedJob = JsonConvert.DeserializeObject<VideoTranscodingJob>(json,
                                            _jsonSerializerSettings);
                                        break;
                                    case JobType.VideoMp4box:
                                        receivedJob = JsonConvert.DeserializeObject<Mp4boxJob>(json,
                                            _jsonSerializerSettings);
                                        break;
                                    case JobType.VideoMerge:
                                        receivedJob = JsonConvert.DeserializeObject<MergeJob>(json,
                                            _jsonSerializerSettings);
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }

                                _progress = new TimeSpan();
                            }
                        }
                    }

                    if (receivedJob != null)
                    {
                        _output.Clear();
                        switch (receivedJob.Type)
                        {
                            case JobType.Audio:
                                ExecuteAudioTranscodingJob((AudioTranscodingJob) receivedJob);
                                break;
                            case JobType.Video:
                            case JobType.VideoMerge:
                                ExecuteVideoTranscodingJob((VideoTranscodingJob) receivedJob);
                                break;
                            case JobType.VideoMp4box:
                                ExecuteMp4BoxJob((Mp4boxJob)receivedJob);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        continue;
                    }

                }
                catch (Exception)
                {
                    
                }

                // Wait 5 seconds before checking for a new job
                // this will prevent a loop taking 100% cpu
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }
        }

        private static void ExecuteAudioTranscodingJob(AudioTranscodingJob job)
        {
            _currentJob = job;
            _currentJob.MachineName = Environment.MachineName;

            using (_commandlineProcess = new Process())
            {
                _commandlineProcess.StartInfo = new ProcessStartInfo
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    FileName = ConfigurationManager.AppSettings["FfmpegPath"],
                    Arguments = job.Arguments
                };

                Console.WriteLine(_commandlineProcess.StartInfo.Arguments);

                _commandlineProcess.OutputDataReceived += Ffmpeg_DataReceived;
                _commandlineProcess.ErrorDataReceived += Ffmpeg_DataReceived;

                TimeSinceLastUpdate.Elapsed += TimeSinceLastUpdate_Elapsed;

                _commandlineProcess.Start();
                _commandlineProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                _commandlineProcess.BeginErrorReadLine();

                TimeSinceLastUpdate.Start();

                _commandlineProcess.WaitForExit();

                if (FfmpegDetectedError())
                {
                    _currentJob.Failed = true;
                    _currentJob.Done = false;
                }
                else
                {
                    _currentJob.Done = _commandlineProcess.ExitCode == 0;
                }

                UpdateProgress().Wait();

                TimeSinceLastUpdate.Stop();
            }
        }

        private static void ExecuteMp4BoxJob(Mp4boxJob receivedJob)
        {
            string pathToMp4Box = ConfigurationManager.AppSettings["Mp4BoxPath"];
            if (string.IsNullOrWhiteSpace(pathToMp4Box)) throw new ArgumentNullException("Mp4BoxPath");
            if (!File.Exists(pathToMp4Box)) throw new FileNotFoundException(pathToMp4Box);

            _currentJob = receivedJob;
            _currentJob.MachineName = Environment.MachineName;

            using (_commandlineProcess = new Process())
            {
                _commandlineProcess.StartInfo = new ProcessStartInfo
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    FileName = pathToMp4Box,
                    Arguments = receivedJob.Arguments
                };

                Console.WriteLine(_commandlineProcess.StartInfo.Arguments);

                _commandlineProcess.ErrorDataReceived += Ffmpeg_DataReceived;

                TimeSinceLastUpdate.Elapsed += TimeSinceLastUpdate_Elapsed;

                _commandlineProcess.Start();
                _commandlineProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                _commandlineProcess.BeginErrorReadLine();

                TimeSinceLastUpdate.Start();

                _commandlineProcess.WaitForExit();

                _currentJob.Done = _commandlineProcess.ExitCode == 0;

                UpdateProgress().Wait();

                TimeSinceLastUpdate.Stop();
            }
        }

        private static void ExecuteVideoTranscodingJob(VideoTranscodingJob videoTranscodingJob)
        {
            _currentJob = videoTranscodingJob;
            _currentJob.MachineName = Environment.MachineName;

            for (int i = 0; i < videoTranscodingJob.Arguments.Length; i++)
            {
                string arguments = videoTranscodingJob.Arguments[i];

                using (_commandlineProcess = new Process())
                {
                    _commandlineProcess.StartInfo = new ProcessStartInfo
                    {
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        FileName = ConfigurationManager.AppSettings["FfmpegPath"],
                        Arguments = arguments
                    };

                    Console.WriteLine(_commandlineProcess.StartInfo.Arguments);

                    _commandlineProcess.OutputDataReceived += Ffmpeg_DataReceived;
                    _commandlineProcess.ErrorDataReceived += Ffmpeg_DataReceived;

                    TimeSinceLastUpdate.Elapsed += TimeSinceLastUpdate_Elapsed;

                    _commandlineProcess.Start();
                    _commandlineProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                    _commandlineProcess.BeginErrorReadLine();

                    TimeSinceLastUpdate.Start();

                    _commandlineProcess.WaitForExit();

                    if (FfmpegDetectedError())
                    {
                        _currentJob.Failed = true;
                        _currentJob.Done = false;
                    }
                    else
                    {
                        _currentJob.Done = _commandlineProcess.ExitCode == 0;
                    }

                    bool isLastCommand = i == videoTranscodingJob.Arguments.Length - 1;
                    if (isLastCommand && _currentJob.Done)
                    {
                        var matches = Regex.Matches(_output.ToString(), @"^\[libx264 @ \w+?\] PSNR Mean.+Avg:([\d\.]+)",
                            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
                        if (matches.Count > 0)
                        {
                            var job = (VideoTranscodingJob)_currentJob;
                            var parts = job.Chunks.ToList();

                            for (int j = 0; j < matches.Count; j++)
                            {
                                parts[j].Psnr = Convert.ToSingle(matches[j].Groups[1].Value, NumberFormatInfo.InvariantInfo);
                            }
                        }
                    }

                    UpdateProgress().Wait();

                    TimeSinceLastUpdate.Stop();
                }
            }
        }

        private static bool FfmpegDetectedError()
        {
            return Regex.IsMatch(_output.ToString(), @"\] Error",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        private static void TimeSinceLastUpdate_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (_commandlineProcess.HasExited)
                return;

            _commandlineProcess.Kill();
            Console.WriteLine("Timed out..");
        }

        private static void Ffmpeg_DataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
                return;

            _output.AppendLine(e.Data);

            var match = Regex.Match(e.Data, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (match.Success)
            {
                TimeSinceLastUpdate.Stop();

                _progress = new TimeSpan(0, Convert.ToInt32(match.Groups[1].Value),
                    Convert.ToInt32(match.Groups[2].Value), Convert.ToInt32(match.Groups[3].Value),
                    Convert.ToInt32(match.Groups[4].Value)*25);

                _currentJob.Progress = _progress;
                UpdateProgress().Wait();

                Console.WriteLine(_progress);

                TimeSinceLastUpdate.Start();
            }
        }

        private static async Task UpdateProgress()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    await client.PutAsync(new Uri(string.Concat(ConfigurationManager.AppSettings["ServerUrl"], "/status")),
                        new StringContent(JsonConvert.SerializeObject(_currentJob, _jsonSerializerSettings),
                            Encoding.ASCII, "application/json"));
                }
            }
            catch
            {
                
            }
        }
    }
}
