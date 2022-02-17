// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

module Runtime =

    type [<AbstractClass>] Runtime() =
        abstract Run<'Error, 'Result> : FIO<'Error, 'Result> -> Fiber<'Error, 'Result>
        abstract Interpret<'Error, 'Result> : FIO<'Error, 'Result> -> 'Result

    and [<AbstractClass; Sealed>] Naive<'Error, 'Result> private () =
         inherit Runtime()
         static member Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
            new Fiber<'Error, 'Result>(eff, Naive.Interpret)

         static member internal Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Try<'Error, 'Result> =
             eff.Accept({ 
                 new FIOVisitor with
                     member _.VisitInput<'Error, 'Result>(input : Input<'Error, 'Result>) =
                         Success <| input.Chan.Receive()
                     member _.VisitOutput<'Error, 'Msg>(output : Output<'Error, 'Msg>) =
                         output.Chan.Send output.Msg
                         Success ()
                     member _.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(con : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>) = 
                         let fiber = new Fiber<'FiberError, 'FiberResult>(con.Eff, Naive.Interpret)
                         Naive.Interpret <| con.Cont fiber
                     member _.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(await : Await<'FiberError, 'FiberResult, 'Error, 'Result>) =
                         Naive.Interpret <| (await.Cont <| await.Fiber.Await())
                     member _.VisitSequence<'FIOResult, 'Error, 'Result>(seq : Sequence<'FIOResult, 'Error, 'Result>) =
                         let fiber = new Fiber<'Error, 'FIOResult>(seq.Eff, Naive.Interpret)
                         let result = fiber.Await()
                         match result with
                         | Success res -> Naive.Interpret <| seq.Cont res
                         | Error err   -> Error err
                     member _.VisitOrElse<'Error, 'Result>(orElse : OrElse<'Error, 'Result>) =
                         let fiber = new Fiber<'Error, 'Result>(orElse.Eff, Naive.Interpret)
                         let result = fiber.Await()
                         match result with
                         | Success res -> Success res
                         | Error _     -> let fiber = new Fiber<'Error, 'Result>(orElse.ElseEff, Naive.Interpret)
                                          fiber.Await()
                     member _.VisitOnError<'FIOError, 'Error, 'Result>(catchAll : OnError<'FIOError, 'Error, 'Result>) =
                        let fiber = new Fiber<'FIOError, 'Result>(catchAll.Eff, Naive.Interpret)
                        let result = fiber.Await()
                        match result with
                        | Success res -> Success res
                        | Error err   -> let fiber = new Fiber<'Error, 'Result>(catchAll.Cont err, Naive.Interpret)
                                         fiber.Await()
                     member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                         Success succ.Value
                     member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                         Error fail.Error
             })

    and Default<'Error, 'Result> = Naive<'Error, 'Result>