﻿// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.CommandLine
{
    using System;
    using WixToolset.Extensibility.Services;

    internal class CommandLineContext : ICommandLineContext
    {
        public CommandLineContext(IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public IMessaging Messaging { get; set; }

        public IExtensionManager ExtensionManager { get; set; }

        public string Arguments { get; set; }

        public string[] ParsedArguments { get; set; }
    }
}
