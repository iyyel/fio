(************************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                  *)
(* Copyright (c) 2022-2025, Daniel Larsen and Technical University of Denmark (DTU) *)
(* All rights reserved                                                              *)
(************************************************************************************)

[<AutoOpen>]
module rec FIO.Core.App

open FIO.Runtime
open FIO.Runtime.Advanced

open System.Threading

let maxThreads = 32767
ThreadPool.SetMaxThreads(maxThreads, maxThreads) |> ignore
ThreadPool.SetMinThreads(maxThreads, maxThreads) |> ignore

let internal defaultRuntime = AdvancedRuntime()

[<AbstractClass>]
type FIOApp<'R, 'E>() =

    static member Run<'R, 'E>(app: FIOApp<'R, 'E>) : Unit =
        app.Run()

    static member Run<'R, 'E>(effect: FIO<'R, 'E>) : Unit =
        let fiber = defaultRuntime.Run effect
        printfn $"%A{fiber.AwaitResult()}"

    abstract member effect: FIO<'R, 'E>

    member this.Run() : Unit =
        let fiber = defaultRuntime.Run this.effect
        printfn $"%A{fiber.AwaitResult()}"

    member this.Run(runtime: Runtime) : Unit =
        let fiber = runtime.Run this.effect
        printfn $"%A{fiber.AwaitResult()}"
