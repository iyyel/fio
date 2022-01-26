open FSharp.FIO


module Pingpong =

    let intPing chanInt =
        let x = 10
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

    let intPingpong chanInt =
        FIO.Parallel(intPing chanInt, intPong chanInt)

    let strPingpong chanStr =
        FIO.Parallel(strPing chanStr, strPong chanStr)

    let intStrPingpong chanInt chanStr =
        FIO.Parallel(intPingpong chanInt, strPingpong chanStr)


module Ring =

    let rec repeat n template eff =
        if n = 1 then
            eff
        else 
            repeat (n - 1) template (template eff)

    let channelPair<'a> =
        let chan1 = FIO.Channel<'a>()
        let chan2 = FIO.Channel<'a>()
        (chan1, chan2)

    let spawnSendProcess chanSend chanRecv value name m =
        let eff = 
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %i{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %i{v}"
                    FIO.Return v))
        let template eff = 
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %i{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %i{v}"
                    eff))
        repeat m template eff

    let spawnRecvProcess chanRecv chanSend name m =
        let eff = 
            FIO.Receive(chanRecv, fun v ->
                printfn $"%s{name} received: %i{v}"
                let value = v + 10
                FIO.Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %i{value}"
                    FIO.Return value))
        let template eff = 
            FIO.Receive(chanRecv, fun v ->
                printfn $"%s{name} received: %i{v}"
                let value = v + 10
                FIO.Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %i{value}"
                    eff))
        repeat m template eff

    let ring n m = 
        let chan1 = FIO.Channel<int>()
        let chan2 = FIO.Channel<int>()
        let chan3 = FIO.Channel<int>()
        let chan4 = FIO.Channel<int>()
        let chan5 = FIO.Channel<int>()

        FIO.Parallel(spawnSendProcess chan1 chan5 0 "p1" m, 
            FIO.Parallel(spawnRecvProcess chan1 chan2 "p2" m,
                FIO.Parallel(spawnRecvProcess chan2 chan3 "p3" m, 
                    FIO.Parallel(spawnRecvProcess chan3 chan4 "p4" m, 
                        spawnRecvProcess chan4 chan5 "p5" m))))


[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()

    let result = FIO.NaiveEval(Ring.ring 0 1)
    printfn $"Result: %A{result}"

    0