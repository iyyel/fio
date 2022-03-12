// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO
open System.Threading.Tasks

module Runtime =

    type [<AbstractClass>] Runtime() =
        abstract Run : FIO<'Result, 'Error> -> Fiber<'Result, 'Error>
        abstract Interpret : FIO<'Result, 'Error> -> Result<'Result, 'Error>

// 
// Naive runtime implementation
// 
    and Naive() =
        inherit Runtime()
        override this.Run (eff : FIO<'Result, 'Error>) : Fiber<'Result, 'Error> =
            new Fiber<'Result, 'Error>(eff, this.Interpret)

        override this.Interpret (eff : FIO<'Result, 'Error>) : Result<'Result, 'Error> =
            eff.Accept({
                new Visitor with
                    member _.VisitInput<'Result, 'Error>(input : Input<'Result, 'Error>) =
                        Ok <| input.Chan.Receive()

                    member _.VisitAction<'Result, 'Error>(action : Action<'Result, 'Error>) =
                        Ok <| action.Func ()

                    member _.VisitConcurrent<'FIOResult, 'FIOError, 'Result, 'Error>
                            (con : Concurrent<'FIOResult, 'FIOError, 'Result, 'Error>) = 
                        let fiber = new Fiber<'FIOResult, 'FIOError>(con.Eff, this.Interpret)
                        this.Interpret <| con.Cont fiber

                    member _.VisitAwait<'FIOResult, 'FIOError, 'Result, 'Error>
                            (await : Await<'FIOResult, 'FIOError, 'Result, 'Error>) =
                        this.Interpret <| (await.Cont <| await.Fiber.Await())

                    member _.VisitSequence<'FIOResult, 'Result, 'Error>
                            (seq : Sequence<'FIOResult, 'Result, 'Error>) =
                        let result = this.Interpret <| seq.Eff
                        match result with
                        | Ok res      -> this.Interpret <| seq.Cont res
                        | Error error -> Error error

                    member _.VisitOrElse<'Result, 'Error>(orElse : OrElse<'Result, 'Error>) =
                        let result = this.Interpret <| orElse.Eff
                        match result with
                        | Ok res  -> Ok res
                        | Error _ -> this.Interpret <| orElse.ElseEff

                    member _.VisitOnError<'FIOError, 'Result, 'Error>
                            (onError : OnError<'FIOError, 'Result, 'Error>) =
                        let result = this.Interpret <| onError.Eff
                        match result with
                        | Ok res      -> Ok res
                        | Error error -> this.Interpret <| onError.Cont error

                    member _.VisitRace<'Result, 'Error>(race : Race<'Result, 'Error>) =
                        let fiberA = new Fiber<'Result, 'Error>(race.EffA, this.Interpret)
                        let fiberB = new Fiber<'Result, 'Error>(race.EffB, this.Interpret)
                        let task = Task.WhenAny([fiberA.Task(); fiberB.Task()])
                        task.Result.Result

                    member _.VisitAttempt<'FIOResult, 'FIOError, 'Result, 'Error>
                            (attempt : Attempt<'FIOResult, 'FIOError, 'Result, 'Error>) =
                        let result = this.Interpret <| attempt.Eff
                        match result with
                        | Ok res      -> this.Interpret <| attempt.ContSuccess res
                        | Error error -> this.Interpret <| attempt.ContError error

                    member _.VisitSucceed<'Result, 'Error>(succ : Succeed<'Result, 'Error>) =
                        Ok succ.Result

                    member _.VisitFail<'Result, 'Error>(fail : Fail<'Result, 'Error>) =
                        Error fail.Error
            })

// 
// Advanced runtime implementation
// 
    and Advanced() =
        inherit Naive()

    and Default() =
        inherit Naive()
