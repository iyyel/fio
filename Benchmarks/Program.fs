(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Program

open System.Threading

open FSharp.FIO
open Benchmarks.Benchmark
open System

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

module ThesisExamples =

    let pureFunc (x : int) (y : int) : int =
        x + y

    let mutable y : int = 42

    let impureFunc (x : int) : int =
        x + y

    let helloWorldExample1 () =
        let helloWorld : FIO<string, obj> = succeed "Hello world!"
        let fiber : Fiber<string, obj> = Advanced.Runtime().Run helloWorld
        let result : Result<string, obj> = fiber.Await()
        printfn $"%A{result}"

    let helloWorldExample2 () =
        let helloWorld = succeed "Hello world!"
        let fiber = Advanced.Runtime().Run helloWorld
        let result = fiber.Await()
        printfn $"%A{result}"

    let concurrencyExample () =
        let spawner = spawn (succeed 42) >> fun fiber ->
                      await fiber >> fun result ->
                      succeed result
        let fiber = Advanced.Runtime().Run spawner
        let result = fiber.Await()
        printfn $"%A{result}"

    let pingPongMpExample () =
        let pinger chan1 chan2 =
          let ping = "ping"
          send ping chan1 >> fun _ ->
          printfn $"pinger sent: %s{ping}"
          receive chan2 >> fun pong ->
          printfn $"pinger received: %s{pong}"
          stop

        let ponger chan1 chan2 =
          receive chan1 >> fun ping ->
          printfn $"ponger received: %s{ping}"
          let pong = "pong"
          send pong chan2 >> fun _ ->
          printfn $"ponger sent: %s{pong}"
          stop

        let chan1 = Channel<string>()
        let chan2 = Channel<string>()
        let pingpong = pinger chan1 chan2 |||
                       ponger chan1 chan2
        let fiber = Advanced.Runtime().Run pingpong
        let result = fiber.Await()
        printfn $"%A{result}"

    let highConcurrencyExample () =
        let sender chan id =
          let msg = 42
          send msg chan >> fun _ ->
          printfn $"Sender[%i{id}] sent: %i{msg}"
          stop

        let rec receiver chan count =
          if count = 0 then
            stop
          else
            receive chan >> fun msg ->
            printfn $"Receiver received: %i{msg}"
            receiver chan (count - 1)

        let rec create chan count acc =
          if count = 0 then
            acc
          else
            let newAcc = sender chan count |||* acc
            create chan (count - 1) newAcc

        let fiberCount = 10000
        let chan = Channel<int>()
        let acc = sender chan fiberCount |||*
                  receiver chan fiberCount
        let program = create chan (fiberCount - 1) acc
        let fiber = Advanced.Runtime().Run program
        let result = fiber.Await()
        printfn $"%A{result}"

    let spawnFiberExample () =
        let effect = 
            spawn (succeed 42) >> fun fiber ->
            await fiber >> fun result ->
            succeed result
        ()

    let externalServices () =
        let readFromDatabase (rand : Random) =
            if rand.NextInt64(0, 2) = 0L then
                succeed "data"
            else
                fail "Database not available!"

        let awaitWebservice (rand : Random) =
            if rand.NextInt64(0, 2) = 1L then
                succeed 100
            else
                fail "Webservice not available!"

        let rand = Random()
        let externalData = readFromDatabase rand ||| awaitWebservice rand
        let defaultData = succeed ("default", 42)
        let program = externalData.onError defaultData
  
        let fiber = Advanced.Runtime().Run program
        let result = fiber.Await()
        printfn $"%A{result}"

let runBenchmarks parsedArgs =
    let configs, runtime, runs, fiberIncrement = parsedArgs
    Run configs runtime runs fiberIncrement

[<EntryPoint>]
let main args =
    let parser = ArgParser.Parser()
    parser.PrintArgs args
    runBenchmarks <| parser.ParseArgs args
    0
