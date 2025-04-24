namespace HydrusTagger

open System.CommandLine

module CommandLineExtensions =
    let addGlobalOption option (command: RootCommand) =
        command.AddGlobalOption option
        command

    let addGlobalArgument argument (command: RootCommand) =
        command.AddArgument argument
        command

    let setGlobalHandler handler argument (command: RootCommand) =
        command.SetHandler(handler, argument)
        command

    let setGlobalHandler2 handler argument1 argument2 (command: RootCommand) =
        command.SetHandler(handler, argument1, argument2)
        command

    let invoke (argv: string array) (rc: RootCommand) = rc.Invoke argv
