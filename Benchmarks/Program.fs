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

let runBenchmarks parsedArgs =
    let configs, runtime, runs, processIncrement = parsedArgs
    Run configs runtime runs processIncrement

let helloWorldExample1 () =
    let effect : FIO<string, obj> = Succeed "Hello world!"
    let fiber : Fiber<string, obj> = Naive.Runtime().Run effect
    let result : Result<string, obj> = fiber.Await()
    printfn $"%A{result}"

let helloWorldExample2 () =
    let effect = Succeed "Hello world!"
    let fiber = Naive.Runtime().Run effect
    let result = fiber.Await()
    printfn $"%A{result}"

let smallConcurrencyExample () =
    let pingerEffect chan1 chan2 =
      let pingMsg = "ping"
      Send pingMsg chan1 >> fun _ ->
      printfn $"pinger sent: %s{pingMsg}"
      Receive chan2 >> fun pongResponse ->
      printfn $"pinger received: %s{pongResponse}"
      End()

    let pongerEffect chan1 chan2 =
      Receive chan1 >> fun pingMsg ->
      printfn $"ponger received: %s{pingMsg}"
      let pongResponse = "pong"
      Send pongResponse chan2 >> fun _ ->
      printfn $"ponger sent: %s{pongResponse}"
      End()

    let chan1 = Channel<string>()
    let chan2 = Channel<string>()
    let effect = Parallel (pingerEffect chan1 chan2)
                          (pongerEffect chan1 chan2)
    let fiber = Naive.Runtime().Run effect
    let result = fiber.Await()
    printfn $"%A{result}"

let bigConcurrencyExample () =
    let senderEffect chan id =
      let msg = 42
      Send msg chan >> fun _ ->
      printfn $"Sender[%i{id}] sent: %i{msg}"
      End()

    let rec receiverEffect chan count =
      if count = 0 then
        End()
      else
        Receive chan >> fun msg ->
        printfn $"Receiver got: %i{msg}"
        receiverEffect chan (count - 1)

    let rec createEffect chan count acc =
      if count = 0 then
        acc
      else
        let newCount = count - 1
        let newAcc = Parallel (senderEffect chan newCount) acc
                     >> fun (_, _) -> End()
        createEffect chan newCount newAcc

    let fiberCount = 3000
    let chan = Channel<int>()
    let acc = Parallel (senderEffect chan fiberCount) 
                       (receiverEffect chan fiberCount)
              >> fun (_, _) -> End()
    let effect = createEffect chan fiberCount acc
    let fiber = Naive.Runtime().Run effect
    let result = fiber.Await()
    printfn $"%A{result}"

(*
let test () =
    let fiber = new Fiber<int, obj>()
    let effect = 
        Sequence (
        Concurrent (Success 42, fiber, fiber.ToLowLevel()), 
        fun innerFiber -> AwaitFiber ((innerFiber :?> Fiber<int, obj>).ToLowLevel()))
    effect
    *)

[<EntryPoint>]
let main args =
    let parser = ArgParser.Parser()
    parser.PrintArgs args
    runBenchmarks <| parser.ParseArgs args
    0
