// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO
open System.Threading.Tasks

module Runtime =

    [<AbstractClass; Sealed>]
    type Naive<'Error, 'Result> private () =
        static member Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new Fiber<'Error, 'Result>(eff, Naive.Interpret)

        static member internal Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                        Success <| input.Chan.Receive()

                    member _.VisitAction<'Error, 'Result>(action : Action<'Error, 'Result>) =
                        Success <| action.Func ()

                    member _.VisitConcurrent<'FIOError, 'FIOResult, 'Error, 'Result>
                            (con : Concurrent<'FIOError, 'FIOResult, 'Error, 'Result>) = 
                        let fiber = new Fiber<'FIOError, 'FIOResult>(con.Eff, Naive.Interpret)
                        Naive.Interpret <| con.Cont fiber

                    member _.VisitAwait<'FIOError, 'FIOResult, 'Error, 'Result>
                            (await : Await<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        Naive.Interpret <| (await.Cont <| await.Fiber.Await())

                    member _.VisitSequence<'FIOResult, 'Error, 'Result>
                            (seq : Sequence<'FIOResult, 'Error, 'Result>) =
                        let result = Naive.Interpret <| seq.Eff
                        match result with
                        | Success res -> Naive.Interpret <| seq.Cont res
                        | Error error -> Error error

                    member _.VisitOrElse<'Error, 'Result>(orElse : OrElse<'Error, 'Result>) =
                        let result = Naive.Interpret <| orElse.Eff
                        match result with
                        | Success res -> Success res
                        | Error _     -> Naive.Interpret <| orElse.ElseEff

                    member _.VisitOnError<'FIOError, 'Error, 'Result>
                            (onError : OnError<'FIOError, 'Error, 'Result>) =
                        let result = Naive.Interpret <| onError.Eff
                        match result with
                        | Success res -> Success res
                        | Error error -> Naive.Interpret <| onError.Cont error

                    member _.VisitRace<'Error, 'Result>(race : Race<'Error, 'Result>) =
                        let fiberA = new Fiber<'Error, 'Result>(race.EffA, Naive.Interpret)
                        let fiberB = new Fiber<'Error, 'Result>(race.EffB, Naive.Interpret)
                        let task = Task.WhenAny([fiberA.Task(); fiberB.Task()])
                        task.Result.Result

                    member _.VisitAttempt<'FIOError, 'FIOResult, 'Error, 'Result>
                            (attempt : Attempt<'FIOError, 'FIOResult, 'Error, 'Result>) =
                        let result = Naive.Interpret <| attempt.Eff
                        match result with
                        | Success res -> Naive.Interpret <| attempt.ContSuccess res
                        | Error error -> Naive.Interpret <| attempt.ContError error

                    member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                        Success succ.Result

                    member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                        Error fail.Error
            })

    and Default<'Error, 'Result> = Naive<'Error, 'Result>