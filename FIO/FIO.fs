namespace FSharp.FIO

open System.Collections.Generic
open System.Threading.Tasks

module FIO =

    type Channel<'a>() =
        let q = Queue<'a>()
        
        member internal this.send v =
                q.Enqueue v
        
        member internal this.receive() =
                if q.Count = 0 then
                    Task.Delay(1) |> ignore
                    this.receive()
                else
                    q.Dequeue()
            
    type Effect<'a> =
        | Input of Channel<'a> * ('a -> Effect<'a>)
        | Output of 'a * Channel<'a> * (unit -> Effect<'a>)
        | Parallel of Effect<'a> * Effect<'a>
        | Return of 'a

    let send (v, c, f) = Output(v, c, f)
    let receive (c, f) = Input(c, f)
    
    let rec naiveEval (e : Effect<'a>) : 'a =
        match e with 
        | Input(c, f)      -> let v = c.receive()
                              naiveEval(f v)
        | Output(v, c, f)  -> c.send v
                              naiveEval(f ())
        | Parallel(e1, e2) -> async {
                                naiveEval e1 |> ignore // TODO: We should not ignore the result of the 'e1' effect.
                              } |> Async.Start
                              naiveEval e2
        | Return v         -> v