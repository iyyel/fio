open FSharp.FIO

let chanInt = FIO.Channel<int>()

let p1 chan =
    let x = 0
    FIO.Send(x, chan, fun () ->
        printfn $"p1 sent: %i{x}"
        FIO.Receive(chan, fun y -> 
            printfn $"p1 received: %i{y}"
            FIO.Send(y, chan, fun () ->
                printfn $"p1 sent: %i{y}"
                FIO.Receive(chan, fun z -> 
                    printfn $"p1 received: %i{z}"
                    FIO.Return z))))

let p2 chan =
    FIO.Receive(chan, fun x -> 
        printfn $"p2 received: %i{x}"
        let y = x + 10
        FIO.Send(y, chan, fun () -> 
            printfn $"p2 sent: %i{y}"
            FIO.Receive(chan, fun z -> 
                printfn $"p2 received: %i{z}"
                let v = z + 10
                FIO.Send(v, chan, fun () -> 
                    printfn $"p2 sent: %i{v}"
                    FIO.Return v))))

let p3 chan =
    FIO.Concurrent(p1 chan, fun t1 ->
        FIO.Concurrent(p2 chan, fun t2 ->
            FIO.Await(t1, fun res1 ->
                FIO.Await(t2, fun res2 ->
                    FIO.Return(res1 + res2)))))

[<EntryPoint>]
let main argv =

    let result = FIO.NaiveEval(p3 chanInt)
    printfn $"Result: %A{result}"
    
    0