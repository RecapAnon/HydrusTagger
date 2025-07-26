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

    let setGlobalHandler3 handler argument1 argument2 argument3 (command: RootCommand) =
        command.SetHandler(handler, argument1, argument2, argument3)
        command

    let setGlobalHandler4 handler argument1 argument2 argument3 argument4 (command: RootCommand) =
        command.SetHandler(handler, argument1, argument2, argument3, argument4)
        command

    let setGlobalHandler5 handler argument1 argument2 argument3 argument4 argument5 (command: RootCommand) =
        command.SetHandler(handler, argument1, argument2, argument3, argument4, argument5)
        command

    let setGlobalHandler6 handler argument1 argument2 argument3 argument4 argument5 argument6 (command: RootCommand) =
        command.SetHandler(handler, argument1, argument2, argument3, argument4, argument5, argument6)
        command

    let invoke (argv: string array) (rc: RootCommand) = rc.Invoke argv

    open System.CommandLine.Binding
    open Microsoft.Extensions.Hosting
    open Microsoft.Extensions.DependencyInjection

    let srvBinder<'T when 'T : not null> (host: IHost) =
        { new BinderBase<'T>() with
            override _.GetBoundValue(bindingContext) = host.Services.GetRequiredService<'T>() }
