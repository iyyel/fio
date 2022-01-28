module FSharp.FIO

open System.Collections.Concurrent

type Channel<'Result>() =
    let queue = ConcurrentQueue<'Result>()

    member internal this.Send value =
        queue.Enqueue value

    member internal this.Receive =
        let status, value = queue.TryDequeue()
        if status then value else this.Receive

type EffectVisitor =
    abstract member VisitInput<'Result> : Input<'Result> -> 'Result
    abstract member VisitOutput<'Result> : Output<'Result> -> 'Result
    abstract member VisitConcurrent<'Result, 'Async> : Concurrent<'Result, 'Async> -> 'Result
    abstract member VisitAwait<'Result, 'Async> : Await<'Result, 'Async> -> 'Result
    abstract member VisitReturn<'Result> : Return<'Result> -> 'Result
and [<AbstractClass>] Effect<'Result>() =
    abstract member Visit<'Result> : EffectVisitor -> 'Result
and Input<'Result>(chan : Channel<'Result>, cont : 'Result -> Effect<'Result>) =
    inherit Effect<'Result>()
    member internal this.Chan = chan
    member internal this.Cont = cont
    override this.Visit<'Result>(input) =
        input.VisitInput<'Result>(this)
and Output<'Result>(value : 'Result, chan : Channel<'Result>, cont : unit -> Effect<'Result>) =
    inherit Effect<'Result>()
    member internal this.Value = value
    member internal this.Chan = chan
    member internal this.Cont = cont
    override this.Visit<'Result>(input) =
        input.VisitOutput<'Result>(this)
and Concurrent<'Result, 'Async>(eff : Effect<'Async>, cont : Async<'Async> -> Effect<'Result>) =
    inherit Effect<'Result>()
    member internal this.Eff = eff
    member internal this.Cont = cont
    override this.Visit<'Result>(con) =
        con.VisitConcurrent<'Result, 'Async>(this)
and Await<'Result, 'Async>(task : Async<'Async>, cont : 'Async -> Effect<'Result>) =
    inherit Effect<'Result>()
    member internal this.Task = task
    member internal this.Cont = cont
    override this.Visit<'Result>(await) =
        await.VisitAwait<'Result, 'Async>(this)
and Return<'Result>(value : 'Result) =
    inherit Effect<'Result>()
    member internal this.Value = value
    override this.Visit<'Result>(input) =
        input.VisitReturn<'Result>(this)

let Send(value, chan, cont) = Output(value, chan, cont)
let Receive(chan, cont) = Input(chan, cont)
let Parallel(effA, effB) = Concurrent(effA, fun asyncA ->
                                Concurrent(effB, fun asyncB ->
                                    Await(asyncA, fun resultA ->
                                        Await(asyncB, fun resultB ->
                                            Return((resultA, resultB))))))

let rec NaiveEval<'Result> (eff : Effect<'Result>) =
    eff.Visit(effectVisitor)
and effectVisitor = { new EffectVisitor with
                        member _.VisitInput<'Result>(input : Input<'Result>) =
                            let value = input.Chan.Receive
                            NaiveEval <| input.Cont value
                        member _.VisitOutput<'Result>(output : Output<'Result>) =
                            output.Chan.Send output.Value
                            NaiveEval <| output.Cont ()
                        member _.VisitConcurrent(con) =
                            let work = async {
                                return NaiveEval con.Eff
                            }
                            let async' = Async.AwaitTask <| Async.StartAsTask work
                            NaiveEval <| con.Cont async'
                        member _.VisitAwait(await) =
                            let result = Async.RunSynchronously await.Task
                            NaiveEval <| await.Cont result
                        member _.VisitReturn<'Result>(ret : Return<'Result>) =
                            ret.Value
                    }
