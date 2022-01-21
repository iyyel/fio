open FSharp.FIO

let chanInt = FIO.Channel<int>()
let chanStr = FIO.Channel<string>()

let p1 chanInt =
    let x = 0
    FIO.Send(x, chanInt, fun () ->
        printfn $"p1 sent: %i{x}"
        FIO.Receive(chanInt, fun y ->
            printfn $"p1 received: %i{y}"
            FIO.Send(y, chanInt, fun () ->
                printfn $"p1 sent: %i{y}"
                FIO.Receive(chanInt, fun z ->
                    printfn $"p1 received: %i{z}"
                    FIO.Return z))))

let p2 chanInt =
    FIO.Receive(chanInt, fun x ->
        printfn $"p2 received: %i{x}"
        let y = x + 10
        FIO.Send(y, chanInt, fun () ->
            printfn $"p2 sent: %i{y}"
            FIO.Receive(chanInt, fun z ->
                printfn $"p2 received: %i{z}"
                let v = z + 10
                FIO.Send(v, chanInt, fun () ->
                    printfn $"p2 sent: %i{v}"
                    FIO.Return v))))

let p11 chanStr =
    let x = ""
    FIO.Send(x, chanStr, fun () ->
        printfn $"p11 sent: %s{x}"
        FIO.Receive(chanStr, fun y ->
            printfn $"p11 received: %s{y}"
            FIO.Send(y, chanStr, fun () ->
                printfn $"p11 sent: %s{y}"
                FIO.Receive(chanStr, fun z ->
                    printfn $"p11 received: %s{z}"
                    FIO.Return z))))

let p22 chanStr =
    FIO.Receive(chanStr, fun x ->
        printfn $"p22 received: %s{x}"
        let y = x + "a"
        FIO.Send(y, chanStr, fun () ->
            printfn $"p22 sent: %s{y}"
            FIO.Receive(chanStr, fun z ->
                printfn $"p22 received: %s{z}"
                let v = z + "b"
                FIO.Send(v, chanStr, fun () ->
                    printfn $"p22 sent: %s{v}"
                    FIO.Return v))))

let p chanInt chanStr =
    FIO.Concurrent(p1 chanInt, fun t1 ->
        printfn "p1 start"
        FIO.Concurrent(p2 chanInt, fun t2 ->
            printfn "p2 start"
            FIO.Concurrent(p11 chanStr, fun t11 ->
                printfn "p11 start"
                FIO.Concurrent(p22 chanStr, fun t22 ->
                    printfn "p22 start"
                    FIO.Await(t1, fun intResult ->
                        FIO.Await(t2, fun _ ->
                            FIO.Await(t11, fun strResult ->
                                FIO.Await(t22, fun _ ->
                                    FIO.Return((intResult, strResult))))))))))

[<EntryPoint>]
let main _ =

    let result = FIO.NaiveEval(p chanInt chanStr)
    printfn $"Result: %A{result}"

    0