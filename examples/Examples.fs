open FSharp.FIO

let chanInt = FIO.Channel<int>()
let chanStr = FIO.Channel<string>()

let p1 c =
    let x = 0
    FIO.Send(x, c, fun () ->
        printfn "p1 sent: %i" x
        FIO.Receive(c, fun y ->
            printfn "p1 received: %i" y
            FIO.Send(y, c, fun () ->
                printfn "p1 sent: %i" y
                FIO.Receive(c, fun z -> 
                    printfn "p1 received: %i" z
                    FIO.Return z))))

let p2 c =
    FIO.Receive(c, fun x ->
        printfn "p2 received: %i" x
        let y = x + 10
        FIO.Send(y, c, fun () ->
            printfn "p2 sent: %i" y
            FIO.Receive(c, fun z ->
                printfn "p2 received: %i" z
                let v = z + 10
                FIO.Send(v, c, fun () ->
                    printfn "p2 sent: %i" v
                    FIO.Return v))))

let p11 c =
    let x = ""
    FIO.Send(x, c, fun () ->
        printfn "p1 sent: %s" x
        FIO.Receive(c, fun y ->
            printfn "p1 received: %s" y
            FIO.Send(y, c, fun () ->
                printfn "p1 sent: %s" y
                FIO.Receive(c, fun z ->
                    printfn "p1 received: %s" z
                    FIO.Return z))))

let p22 c =
    FIO.Receive(c, fun x ->
        printfn "p2 received: %s" x
        let y = x + "a"
        FIO.Send(y, c, fun () ->
            printfn "p2 sent: %s" y
            FIO.Receive(c, fun z ->
                printfn "p2 received: %s" z
                let v = z + "b"
                FIO.Send(v, c, fun () ->
                    printfn "p2 sent: %s" v
                    FIO.Return v))))

let p3 chan =
    FIO.Concurrent(p1 chan, fun t1 ->
        FIO.Concurrent(p2 chan, fun t2 ->
            FIO.Await(t1, fun res1 ->
                FIO.Await(t2, fun res2 ->
                    FIO.Return(res1 + res2)))))

let p33 chan =
    FIO.Concurrent(p11 chan, fun t1 ->
        FIO.Concurrent(p22 chan, fun t2 ->
            FIO.Await(t1, fun res1 ->
                FIO.Await(t2, fun res2 ->
                    FIO.Return(res1 + res2)))))

let p10 chanInt chanStr =
    FIO.Concurrent(p3 chanInt, fun t1 ->
        FIO.Concurrent(p33 chanStr, fun t2 ->
            FIO.Await(t1, fun res1 ->
                FIO.Await(t2, fun res2 ->
                    printfn "int result: %i" res1
                    printfn "string result: %s" res2
                    FIO.Return(res1)))))

[<EntryPoint>]
let main _ =

    let result = FIO.NaiveEval(p10 chanInt chanStr)
    printfn "Result: %i" result

    0