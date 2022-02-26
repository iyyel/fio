// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

open FsCheck
open FSharp.FIO.Runtime
open FSharp.FIO.FIO

type InputTests = 
    static member ``Input always retrieves the single element in the channel`` (msg : int) =
        let chan = Channel<int>()
        chan.Send msg
        let fiber = Default.Run <| Input chan
        fiber.Await() = Success msg

    static member ``Input retrieves elements in FIFO order (queue channel)`` (msgs : List<int>) =
        let chan = Channel<int>()

        for msg in msgs do
            chan.Send msg

        let rec recvElements (chan : Channel<int>) es =
            match chan.Size() with
            | 0 -> es
            | _ -> let fiber = Default.Run <| Input chan
                   let result = fiber.Await()
                   match result with
                   | Success msg -> recvElements chan (es @ [msg])
                   | Error _     -> []

        let expected = recvElements chan []
        expected = msgs

type ActionTests =
    static member ``Action always evaluates to the given result`` (result : int) =
        let fiber = Default.Run <| Action (fun () -> result)
        fiber.Await() = Success result

type ConcurrentTests = class end

type AwaitTests = class end

type SequenceTests = class end

type OrElseTests = class end

type OnErrorTests = class end

type RaceTests = class end

type AttemptTests = class end

type SucceedTests =

    static member ``Succeed always evaluates to the same value`` (result : int) =
        let fiber = Default.Run <| Succeed result
        fiber.Await() = Success result

type FailTests = class end

let executeAllTests =
    Check.QuickAll<InputTests>()
    Check.QuickAll<ActionTests>()
    //Check.QuickAll<ConcurrentTests>()
    //Check.QuickAll<AwaitTests>()
    //Check.QuickAll<SequenceTests>()
    //Check.QuickAll<OrElseTests>()
    //Check.QuickAll<OnErrorTests>()
    //Check.QuickAll<RaceTests>()
    //Check.QuickAll<AttemptTests>()
    //Check.QuickAll<SucceedTests>()
    //Check.QuickAll<FailTests>()

[<EntryPoint>]
let main _ =
    executeAllTests
    0
