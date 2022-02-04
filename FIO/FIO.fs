// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module FSharp.FIO

open System.Collections.Concurrent
open System.Threading.Tasks

type Channel<'Msg>() =
    let bc = new BlockingCollection<'Msg>()
    member this.Send value = bc.Add value
    member this.Receive = bc.Take()

type FIOVisitor =
    abstract VisitInput<'Msg, 'Success> : Input<'Msg, 'Success> -> 'Success
    abstract VisitOutput<'Msg, 'Success> : Output<'Msg, 'Success> -> 'Success
    abstract VisitConcurrent<'Async, 'Success> : Concurrent<'Async, 'Success> -> 'Success
    abstract VisitAwait<'Async, 'Success> : Await<'Async, 'Success> -> 'Success
    abstract VisitSucceed<'Success> : Succeed<'Success> -> 'Success
and [<AbstractClass>] FIO<'Success>() =
    abstract Visit<'Success> : FIOVisitor -> 'Success
and Input<'Msg, 'Success>(chan : Channel<'Msg>, cont : 'Msg -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Chan = chan
    member internal this.Cont = cont
    override this.Visit<'Success>(input) =
        input.VisitInput<'Msg, 'Success>(this)
and Output<'Msg, 'Success>(value : 'Msg, chan : Channel<'Msg>, cont : unit -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Value = value
    member internal this.Chan = chan
    member internal this.Cont = cont
    override this.Visit<'Success>(input) =
        input.VisitOutput<'Msg, 'Success>(this)
and Concurrent<'Task, 'Success>(eff : FIO<'Task>, cont : Task<'Task> -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Eff = eff
    member internal this.Cont = cont
    override this.Visit<'Success>(con) =
        con.VisitConcurrent<'Task, 'Success>(this)
and Await<'Task, 'Success>(task : Task<'Task>, cont : 'Task -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Task = task
    member internal this.Cont = cont
    override this.Visit<'Success>(await) =
        await.VisitAwait<'Task, 'Success>(this)
and Succeed<'Success>(value : 'Success) =
    inherit FIO<'Success>()
    member internal this.Value = value
    override this.Visit<'Success>(input) =
        input.VisitSucceed<'Success>(this)

let Send<'Msg, 'Success>(value : 'Msg, chan : Channel<'Msg>, cont : (unit -> FIO<'Success>)) : Output<'Msg, 'Success> = Output(value, chan, cont)
let Receive<'Msg, 'Success>(chan : Channel<'Msg>, cont : ('Msg -> FIO<'Success>)) : Input<'Msg, 'Success> = Input(chan, cont)
let Parallel<'SuccessA, 'SuccessB, 'SuccessC>(effA : FIO<'SuccessA>, effB : FIO<'SuccessB>, cont : ('SuccessA * 'SuccessB -> FIO<'SuccessC>)) : Concurrent<'SuccessA, 'SuccessC>=
    Concurrent(effA, fun taskA ->
        Concurrent(effB, fun taskB ->
            Await(taskA, fun succA ->
                Await(taskB, fun succB ->
                    cont (succA, succB)))))
let End() : Succeed<unit> = Succeed ()

let rec NaiveEval<'Success> (eff : FIO<'Success>) : 'Success =
    eff.Visit({ 
        new FIOVisitor with
            member _.VisitInput<'Msg, 'Success>(input : Input<'Msg, 'Success>) =
                let value = input.Chan.Receive
                NaiveEval <| input.Cont value
            member _.VisitOutput<'Msg, 'Success>(output : Output<'Msg, 'Success>) =
                output.Chan.Send output.Value
                NaiveEval <| output.Cont ()
            member _.VisitConcurrent(con) =
                let task = Task.Factory.StartNew(fun () -> NaiveEval con.Eff)
                NaiveEval <| con.Cont task
            member _.VisitAwait(await) =
                NaiveEval <| await.Cont await.Task.Result
            member _.VisitSucceed<'Success>(succ : Succeed<'Success>) =
                succ.Value
    })
