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

         static member internal Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : 'Result =
             eff.Accept({ 
                 new FIOVisitor with
                     member _.VisitInput<'Msg, 'Error, 'Result>(input : Input<'Msg, 'Error, 'Result>) =
                         let value = input.Chan.Receive()
                         Naive.Interpret <| input.Cont value
                     member _.VisitOutput<'Msg, 'Error, 'Result>(output : Output<'Msg, 'Error, 'Result>) =
                         output.Chan.Send output.Value
                         Naive.Interpret <| output.Cont ()
                     member _.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(con : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>) = 
                         let fiber = new Fiber<'FiberError, 'FiberResult>(con.Eff, Naive.Interpret)
                         Naive.Interpret <| con.Cont fiber
                     member _.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(await : Await<'FiberError, 'FiberResult, 'Error, 'Result>) =
                         Naive.Interpret <| await.Cont (await.Fiber.Await())
                     member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                         succ.Value
                     member _.VisitFail<'Error, 'Result>(fail : Fail<'Error, 'Result>) =
                         fail.Error
             })

    and Default<'Error, 'Result> = Naive<'Error, 'Result>