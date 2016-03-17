﻿// Copyright 2015 ThoughtWorks, Inc.
//
// This file is part of Gauge-CSharp.
//
// Gauge-CSharp is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Gauge-CSharp is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with Gauge-CSharp.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Gauge.CSharp.Core;
using Gauge.CSharp.Runner.Exceptions;
using Gauge.Messages;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NLog;
using ILogger = Microsoft.Build.Framework.ILogger;

namespace Gauge.CSharp.Runner
{
    public class StartCommand : IGaugeCommand
    {
        private MessageProcessorFactory _messageProcessorFactory;

        private static readonly Logger Logger = LogManager.GetLogger("Build");

        [DebuggerHidden]
        public void Execute()
        {
            Initialize();
            try
            {
                using (var gaugeConnection = new GaugeConnection(new TcpClientWrapper(Utils.GaugePort)))
                {
                    while (gaugeConnection.Connected)
                    {
                        var messageBytes = gaugeConnection.ReadBytes();
                        var message = Message.ParseFrom(messageBytes.ToArray());

                        var processor = _messageProcessorFactory.GetProcessor(message.MessageType);
                        var response = processor.Process(message);
                        gaugeConnection.WriteMessage(response);
                        if (message.MessageType == Message.Types.MessageType.KillProcessRequest)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
            }
        }

        private void Initialize()
        {
            var customBuildPath = Environment.GetEnvironmentVariable("gauge_custom_build_path");
            if (string.IsNullOrEmpty(customBuildPath))
            {
                try
                {
                    BuildTargetGaugeProject();
                }
                catch (NotAValidGaugeProjectException)
                {
                    Logger.Fatal("Cannot locate a Project File in {0}", Utils.GaugeProjectRoot);
                    Environment.Exit(1);
                }
            }
            Logger.Info("Creating a Sandbox in: {0}", Utils.GetGaugeBinDir());
            try
            {
                var sandbox = SandboxFactory.Create();
                _messageProcessorFactory = new MessageProcessorFactory(sandbox);
            }
            catch (Exception e)
            {
                Logger.Info("Unable to create sandbox in {0}", Utils.GetGaugeBinDir());
                Logger.Fatal(e.ToString);
                Environment.Exit(1);
            }
        }

        private static void BuildTargetGaugeProject()
        {
            var consoleLogger = new ConsoleLogger(LoggerVerbosity.Quiet);
            var solutionFileList = Directory.GetFiles(Utils.GaugeProjectRoot, "*.sln");

            if (!solutionFileList.Any())
            {
                throw new NotAValidGaugeProjectException();
            }
            var solutionFullPath = solutionFileList.First();
            var gaugeBinDir = Utils.GetGaugeBinDir();
            try
            {
                Logger.Debug("Create Gauge Bin Directory: {0}", gaugeBinDir);
                Directory.CreateDirectory(gaugeBinDir);
            }
            catch (IOException ex)
            {
                Logger.Fatal(ex, "Unable to create Gauge Bin Directory in {0}", gaugeBinDir);
                Environment.Exit(1);
            }
            Logger.Info("Building Project: {0}", solutionFullPath);
            var pc = new ProjectCollection();
            var globalProperty = new Dictionary<string, string>
            {
                {"Configuration", "Release"},
                {"Platform", "Any CPU"},
                {"OutputPath", gaugeBinDir}
            };

            var buildRequestData = new BuildRequestData(solutionFullPath, globalProperty, null, new[] {"Build"}, null);

            var errorCodeAggregator = new ErrorCodeAggregator();
            var buildParameters = new BuildParameters(pc) {Loggers = new ILogger[] {consoleLogger, errorCodeAggregator}};

            var buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequestData);

            if (errorCodeAggregator.ErrorCodes.Contains("CS1001"))
            {
                Logger.Error("You have chosen an invalid folder name to initialize a Gauge project.");
                Logger.Error("Please choose a project name that complies with C# Project naming conventions.");
            }

            Logger.Info(buildResult.OverallResult);
        }
    }
}