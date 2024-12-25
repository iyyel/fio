(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.App

open FIO.Core

open FIO.Runtime
open FIO.Runtime.Advanced

open System.Threading

let private maxThreads = 32767
ThreadPool.SetMaxThreads(maxThreads, maxThreads) |> ignore
ThreadPool.SetMinThreads(maxThreads, maxThreads) |> ignore

let private defaultRuntime = AdvancedRuntime()

let private printResult (fiber: Fiber<'R, 'E>) =
    printfn $"%A{fiber.AwaitResult()}"

[<AbstractClass>]
type FIOApp<'R, 'E>(runtime: Runtime) =

    new() = FIOApp(defaultRuntime)

    static member Run(app: FIOApp<'R, 'E>) : unit =
        app.Run()

    static member Run(app: FIOApp<'R, 'E>, runtime: Runtime) : unit =
        app.Run(runtime)

    static member Run(effect: FIO<'R, 'E>) : unit =
        let fiber = defaultRuntime.Run effect
        printResult fiber

    abstract member effect: FIO<'R, 'E>

    member this.Run() : unit =
        let fiber = runtime.Run this.effect
        printResult fiber

    member this.Run(runtime: Runtime) : unit =
        let fiber = runtime.Run this.effect
        printResult fiber
