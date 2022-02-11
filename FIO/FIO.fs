﻿// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

type Channel<'Msg>() =
    let bc = new BlockingCollection<'Msg>()
    member _.Send value = bc.Add value
    member _.Receive() = bc.Take()

type Fiber<'Error, 'Result>(work : 'Result) =
    let task = Task.Factory.StartNew(fun () -> work)
    member _.Await() = task.Result
    member _.OrElse(effA : FIO<'ErrorA, 'ResultA>) = () // try this effect (Eff), however, if it fails, then try another one (EffA).
    member _.CatchAll(cont : 'Error -> FIO<'ErrorA, 'ResultA>) = () // catch all errors and recover from it effectfully. 

and FIOVisitor =
    abstract VisitInput<'Msg, 'Error, 'Result>                           : Input<'Msg, 'Error, 'Result> -> 'Result
    abstract VisitOutput<'Msg, 'Error, 'Result>                          : Output<'Msg, 'Error, 'Result> -> 'Result
    abstract VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result> : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result> -> 'Result
    abstract VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>      : Await<'FiberError, 'FiberResult, 'Error, 'Result> -> 'Result
    abstract VisitSucceed<'Error, 'Result>                               : Succeed<'Error, 'Result> -> 'Result

and [<AbstractClass>] FIO<'Error, 'Result>() =
    abstract Accept<'Error, 'Result> : FIOVisitor -> 'Result

and Input<'Msg, 'Error, 'Result>
        (chan : Channel<'Msg>,
         cont : 'Msg -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitInput<'Msg, 'Error, 'Result>(this)

and Output<'Msg, 'Error, 'Result>
        (value : 'Msg,
          chan : Channel<'Msg>,
          cont : unit -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Value = value
    member internal _.Chan = chan
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitOutput<'Msg, 'Error, 'Result>(this)

and Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>
        (eff : FIO<'FiberError, 'FiberResult>,
        cont : Fiber<'FiberError, 'FiberResult> -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Eff = eff
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(this)

and Await<'FiberError, 'FiberResult, 'Error, 'Result>
        (fiber : Fiber<'FiberError, 'FiberResult>,
          cont : 'FiberResult -> FIO<'Error, 'Result>) =
    inherit FIO<'Error, 'Result>()
    member internal _.Fiber = fiber
    member internal _.Cont = cont
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(this)

and Succeed<'Error, 'Result>(value : 'Result) =
    inherit FIO<'Error, 'Result>()
    member internal _.Value = value
    override this.Accept<'Error, 'Result>(visitor) =
        visitor.VisitSucceed<'Error, 'Result>(this)

and [<AbstractClass>] Runtime() =
    abstract Run<'Error, 'Result> : FIO<'Error, 'Result> -> Fiber<'Error, 'Result>
    abstract Interpret<'Error, 'Result> : FIO<'Error, 'Result> -> 'Result

and [<AbstractClass; Sealed>] Naive<'Error, 'Result> private () =
     inherit Runtime()
     static member Run<'Error, 'Result> (eff : FIO<'Error, 'Result>) : Fiber<'Error, 'Result> =
        new Fiber<'Error, 'Result>(Naive.Interpret eff)

     static member Interpret<'Error, 'Result> (eff : FIO<'Error, 'Result>) : 'Result =
         eff.Accept({ 
             new FIOVisitor with
                 member _.VisitInput<'Msg, 'Error, 'Result>(input : Input<'Msg, 'Error, 'Result>) =
                     let value = input.Chan.Receive()
                     Naive.Interpret <| input.Cont value
                 member _.VisitOutput<'Msg, 'Error, 'Result>(output : Output<'Msg, 'Error, 'Result>) =
                     output.Chan.Send output.Value
                     Naive.Interpret <| output.Cont ()
                 member _.VisitConcurrent<'FiberError, 'FiberResult, 'Error, 'Result>(con : Concurrent<'FiberError, 'FiberResult, 'Error, 'Result>) = 
                     let fiber = new Fiber<'FiberError, 'FiberResult>(Naive.Interpret con.Eff)
                     printfn "in concurrent"
                     Naive.Interpret <| con.Cont fiber
                 member _.VisitAwait<'FiberError, 'FiberResult, 'Error, 'Result>(await : Await<'FiberError, 'FiberResult, 'Error, 'Result>) =
                     printfn "in await"
                     Naive.Interpret <| await.Cont (await.Fiber.Await())
                 member _.VisitSucceed<'Error, 'Result>(succ : Succeed<'Error, 'Result>) =
                     succ.Value
         })

and Default<'Error, 'Result> = Naive<'Error, 'Result>

let Send<'Msg, 'Error, 'Result>
    (value : 'Msg,
      chan : Channel<'Msg>,
      cont : unit -> FIO<'Error, 'Result>)
           : Output<'Msg, 'Error, 'Result> =
    Output(value, chan, cont)

let Receive<'Msg, 'Error, 'Result>
    (chan : Channel<'Msg>,
     cont : 'Msg -> FIO<'Error, 'Result>)
          : Input<'Msg, 'Error, 'Result> =
    Input(chan, cont)

let Parallel<'ErrorA, 'ResultA, 'ErrorB, 'ResultB, 'ErrorC, 'ResultC>
    (effA : FIO<'ErrorA, 'ResultA>,
     effB : FIO<'ErrorB, 'ResultB>,
     cont : 'ResultA * 'ResultB -> FIO<'ErrorC, 'ResultC>) =
    Concurrent(effA, fun taskA ->
        Concurrent(effB, fun taskB ->
            Await(taskA, fun resA ->
                Await(taskB, fun resB ->
                    cont (resA, resB)))))

let End() : Succeed<unit, unit> =
    Succeed ()