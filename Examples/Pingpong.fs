module Pingpong

open FSharp.FIO

let intPing chanInt =
    let x = 10
    FIO.Send(x, chanInt, fun () ->
        printfn $"intPing sent: %A{x}"
        FIO.Receive(chanInt, fun y ->
            printfn $"intPing received: %A{y}"
            FIO.Send(y, chanInt, fun () ->
                printfn $"intPing sent: %A{y}"
                FIO.Receive(chanInt, fun z ->
                    printfn $"intPing received: %A{z}"
                    FIO.Return z))))

let intPong chanInt =
    FIO.Receive(chanInt, fun x ->
        printfn $"intPong received: %A{x}"
        let y = x + 10
        FIO.Send(y, chanInt, fun () ->
            printfn $"intPong sent: %A{y}"
            FIO.Receive(chanInt, fun z ->
                printfn $"intPong received: %A{z}"
                let v = z + 10
                FIO.Send(v, chanInt, fun () ->
                    printfn $"intPong sent: %A{v}"
                    FIO.Return v))))

let rec intPingInf chanInt =
    let x = 10
    FIO.Send(x, chanInt, fun () ->
        printfn $"intPing sent: %A{x}"
        FIO.Receive(chanInt, fun y ->
            printfn $"intPing received: %A{y}"
            intPingInf chanInt))

let rec intPongInf chanInt =
    FIO.Receive(chanInt, fun x ->
        printfn $"intPong received: %A{x}"
        let y = x + 10
        FIO.Send(y, chanInt, fun () ->
            printfn $"intPong sent: %A{y}"
            intPongInf chanInt))

let strPing chanStr =
    let x = ""
    FIO.Send(x, chanStr, fun () ->
        printfn $"strPing sent: %A{x}"
        FIO.Receive(chanStr, fun y ->
            printfn $"strPing received: %A{y}"
            FIO.Send(y, chanStr, fun () ->
                printfn $"strPing sent: %A{y}"
                FIO.Receive(chanStr, fun z ->
                    printfn $"strPing received: %A{z}"
                    FIO.Return z))))

let strPong chanStr =
    FIO.Receive(chanStr, fun x ->
        printfn $"strPong received: %A{x}"
        let y = x + "a"
        FIO.Send(y, chanStr, fun () ->
            printfn $"strPong sent: %A{y}"
            FIO.Receive(chanStr, fun z ->
                printfn $"strPong received: %A{z}"
                let v = z + "b"
                FIO.Send(v, chanStr, fun () ->
                    printfn $"strPong sent: %A{v}"
                    FIO.Return v))))

let rec strPingInf chanStr =
    let x = ""
    FIO.Send(x, chanStr, fun () ->
        printfn $"strPing sent: %A{x}"
        FIO.Receive(chanStr, fun y ->
            printfn $"strPing received: %A{y}"
            strPingInf chanStr))

let rec strPongInf chanStr =
    FIO.Receive(chanStr, fun x ->
        printfn $"strPong received: %A{x}"
        let y = x + "a"
        FIO.Send(y, chanStr, fun () ->
            printfn $"strPong sent: %A{y}"
            strPongInf chanStr))

let intPingpong chanInt =
    FIO.Parallel(intPing chanInt, intPong chanInt)

let intPingpongInf chanInt =
    FIO.Parallel(intPingInf chanInt, intPongInf chanInt)

let strPingpong chanStr =
    FIO.Parallel(strPing chanStr, strPong chanStr)

let strPingpongInf chanStr =
    FIO.Parallel(strPingInf chanStr, strPongInf chanStr)

let intStrPingpong chanInt chanStr =
    FIO.Parallel(intPingpong chanInt, strPingpong chanStr)

let intStrPingpongInf chanInt chanStr =
    FIO.Parallel(intPingpongInf chanInt, strPingpongInf chanStr)
