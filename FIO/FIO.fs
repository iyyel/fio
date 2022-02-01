// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module FSharp.FIO

open System.Collections.Concurrent

type Channel<'Msg>() =
    let queue = ConcurrentQueue<'Msg>()

    member internal this.Send value =
        queue.Enqueue value

    member internal this.Receive =
        let status, value = queue.TryDequeue()
        if status then value else this.Receive

type FIOVisitor =
    abstract member VisitInput<'Msg, 'Success> : Input<'Msg, 'Success> -> 'Success
    abstract member VisitOutput<'Msg, 'Success> : Output<'Msg, 'Success> -> 'Success
    abstract member VisitConcurrent<'Async, 'Success> : Concurrent<'Async, 'Success> -> 'Success
    abstract member VisitAwait<'Async, 'Success> : Await<'Async, 'Success> -> 'Success
    abstract member VisitSucceed<'Success> : Succeed<'Success> -> 'Success
and [<AbstractClass>] FIO<'Success>() =
    abstract member Visit<'Success> : FIOVisitor -> 'Success
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
and Concurrent<'Async, 'Success>(eff : FIO<'Async>, cont : Async<'Async> -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Eff = eff
    member internal this.Cont = cont
    override this.Visit<'Success>(con) =
        con.VisitConcurrent<'Async, 'Success>(this)
and Await<'Async, 'Success>(task : Async<'Async>, cont : 'Async -> FIO<'Success>) =
    inherit FIO<'Success>()
    member internal this.Task = task
    member internal this.Cont = cont
    override this.Visit<'Success>(await) =
        await.VisitAwait<'Async, 'Success>(this)
and Succeed<'Success>(value : 'Success) =
    inherit FIO<'Success>()
    member internal this.Value = value
    override this.Visit<'Success>(input) =
        input.VisitSucceed<'Success>(this)

let Send<'Msg, 'Success>(value : 'Msg, chan : Channel<'Msg>, cont : (unit -> FIO<'Success>)) : Output<'Msg, 'Success> = Output(value, chan, cont)
let Receive<'Msg, 'Success>(chan : Channel<'Msg>, cont : ('Msg -> FIO<'Success>)) : Input<'Msg, 'Success> = Input(chan, cont)
let Parallel<'SuccessA, 'SuccessB, 'SuccessC>(effA : FIO<'SuccessA>, effB : FIO<'SuccessB>, cont : ('SuccessA * 'SuccessB -> FIO<'SuccessC>)) : Concurrent<'SuccessA, 'SuccessC>=
    Concurrent(effA, fun asyncA ->
        Concurrent(effB, fun asyncB ->
            Await(asyncA, fun succA ->
                Await(asyncB, fun succB ->
                    cont (succA, succB)))))
let End() : Succeed<unit> = Succeed ()

let rec NaiveEval<'Success> (eff : FIO<'Success>) =
    eff.Visit(fioVisitor)
and fioVisitor = { new FIOVisitor with
                        member _.VisitInput<'Msg, 'Success>(input : Input<'Msg, 'Success>) =
                            let value = input.Chan.Receive
                            NaiveEval <| input.Cont value
                        member _.VisitOutput<'Msg, 'Success>(output : Output<'Msg, 'Success>) =
                            output.Chan.Send output.Value
                            NaiveEval <| output.Cont ()
                        member _.VisitConcurrent(con) =
                            let work = async {
                                return NaiveEval con.Eff
                            }
                            let task = Async.AwaitTask <| Async.StartAsTask work
                            NaiveEval <| con.Cont task
                        member _.VisitAwait(await) =
                            let succ = Async.RunSynchronously await.Task
                            NaiveEval <| await.Cont succ
                        member _.VisitSucceed<'Success>(succ : Succeed<'Success>) =
                            succ.Value
                    }
