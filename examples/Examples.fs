open FSharp.FIO

module Pingpong =

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

    let intPingpong chanInt =
        FIO.Parallel(intPing chanInt, intPong chanInt)

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

    let strPingpong chanStr =
        FIO.Parallel(strPing chanStr, strPong chanStr)

    let intStrPingpong chanInt chanStr =
        FIO.Parallel(intPingpong chanInt, strPingpong chanStr)

[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()

    let result = FIO.NaiveEval(Pingpong.intStrPingpong chanInt chanStr)
    printfn $"intStrPingpong result: %A{result}"

    0