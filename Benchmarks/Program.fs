(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Program

open System.Threading

open FSharp.FIO
open Benchmarks.Benchmark

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

module ThesisExamples =

    let helloWorldExample1 () =
        let effect : FIO<string, obj> = succeed "Hello world!"
        let fiber : Fiber<string, obj> = Advanced.Runtime().Run effect
        let result : Result<string, obj> = fiber.Await()
        printfn $"%A{result}"

    let helloWorldExample2 () =
        let effect = succeed "Hello world!"
        let fiber = Advanced.Runtime().Run effect
        let result = fiber.Await()
        printfn $"%A{result}"

    let concurrencyExample () =
        let effect = spawn (succeed 42) >> fun fiber ->
                     await fiber >> fun result ->
                     succeed result
        let fiber = Advanced.Runtime().Run effect
        let result = fiber.Await()
        printfn $"%A{result}"

    let pingPongMpExample () =
        let pingerEffect chan1 chan2 =
          let pingMsg = "ping"
          send pingMsg chan1 >> fun _ ->
          printfn $"pinger sent: %s{pingMsg}"
          receive chan2 >> fun pongResponse ->
          printfn $"pinger received: %s{pongResponse}"
          stop

        let pongerEffect chan1 chan2 =
          receive chan1 >> fun pingMsg ->
          printfn $"ponger received: %s{pingMsg}"
          let pongResponse = "pong"
          send pongResponse chan2 >> fun _ ->
          printfn $"ponger sent: %s{pongResponse}"
          stop

        let chan1 = Channel<string>()
        let chan2 = Channel<string>()
        let effect = pingerEffect chan1 chan2 |||
                     pongerEffect chan1 chan2
        let fiber = Advanced.Runtime().Run effect
        let result = fiber.Await()
        printfn $"%A{result}"

    let highConcurrencyExample () =
        let senderEffect chan id =
          let msg = 42
          send msg chan >> fun _ ->
          printfn $"Sender[%i{id}] sent: %i{msg}"
          stop

        let rec receiverEffect chan count =
          if count = 0 then
            stop
          else
            receive chan >> fun msg ->
            printfn $"Receiver received: %i{msg}"
            receiverEffect chan (count - 1)

        let rec createEffect chan count acc =
          if count = 0 then
            acc
          else
            let newCount = count - 1
            let newAcc = senderEffect chan newCount |||* acc
            createEffect chan newCount newAcc

        let fiberCount = 4300
        let chan = Channel<int>()
        let acc = senderEffect chan fiberCount |||*
                  receiverEffect chan fiberCount
        let effect = createEffect chan fiberCount acc
        let fiber = Advanced.Runtime().Run effect
        let result = fiber.Await()
        printfn $"%A{result}"

    let spawnFiberExample () =
      let effect = 
        spawn (succeed 42) >> fun fiber ->
        await fiber >> fun result ->
        succeed result
      ()

let runBenchmarks parsedArgs =
    let configs, runtime, runs, processIncrement = parsedArgs
    Run configs runtime runs processIncrement

[<EntryPoint>]
let main args =
    let parser = ArgParser.Parser()
    parser.PrintArgs args
    runBenchmarks <| parser.ParseArgs args
    0
