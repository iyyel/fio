open FSharp.FIO

let chanInt = FIO.Channel<int>()
let chanStr = FIO.Channel<string>()

let intPing chanInt =
    let x = 0
    FIO.Send(x, chanInt, fun () ->
        printfn $"intPing sent: %i{x}"
        FIO.Receive(chanInt, fun y ->
            printfn $"intPing received: %i{y}"
            FIO.Send(y, chanInt, fun () ->
                printfn $"intPing sent: %i{y}"
                FIO.Receive(chanInt, fun z ->
                    printfn $"intPing received: %i{z}"
                    FIO.Return z))))

let intPong chanInt =
    FIO.Receive(chanInt, fun x ->
        printfn $"intPong received: %i{x}"
        let y = x + 10
        FIO.Send(y, chanInt, fun () ->
            printfn $"intPong sent: %i{y}"
            FIO.Receive(chanInt, fun z ->
                printfn $"intPong received: %i{z}"
                let v = z + 10
                FIO.Send(v, chanInt, fun () ->
                    printfn $"intPong sent: %i{v}"
                    FIO.Return v))))

let strPing chanStr =
    let x = ""
    FIO.Send(x, chanStr, fun () ->
        printfn $"strPing sent: %s{x}"
        FIO.Receive(chanStr, fun y ->
            printfn $"strPing received: %s{y}"
            FIO.Send(y, chanStr, fun () ->
                printfn $"strPing sent: %s{y}"
                FIO.Receive(chanStr, fun z ->
                    printfn $"strPing received: %s{z}"
                    FIO.Return z))))

let strPong chanStr =
    FIO.Receive(chanStr, fun x ->
        printfn $"strPong received: %s{x}"
        let y = x + "a"
        FIO.Send(y, chanStr, fun () ->
            printfn $"strPong sent: %s{y}"
            FIO.Receive(chanStr, fun z ->
                printfn $"strPong received: %s{z}"
                let v = z + "b"
                FIO.Send(v, chanStr, fun () ->
                    printfn $"strPong sent: %s{v}"
                    FIO.Return v))))

let pingpong chanInt chanStr =
    FIO.Concurrent(intPing chanInt, fun t1 ->
        printfn "intPing start"
        FIO.Concurrent(intPong chanInt, fun t2 ->
            printfn "intPong start"
            FIO.Concurrent(strPing chanStr, fun t11 ->
                printfn "strPing start"
                FIO.Concurrent(strPong chanStr, fun t22 ->
                    printfn "strPong start"
                    FIO.Await(t1, fun intResult ->
                        FIO.Await(t2, fun _ ->
                            FIO.Await(t11, fun strResult ->
                                FIO.Await(t22, fun _ ->
                                    FIO.Return((intResult, strResult))))))))))

[<EntryPoint>]
let main _ =

    let result = FIO.NaiveEval(pingpong chanInt chanStr)
    printfn $"pingpong result: %A{result}"

    0