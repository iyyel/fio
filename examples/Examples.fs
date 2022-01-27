open FSharp.FIO

module Pingpong =

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

module Ring =

    let rec internal repeat n template eff =
        if n = 1 then
            eff
        else 
            repeat (n - 1) template (template eff)

    let internal spawnSendProcess chanSend chanRecv value name m =
        let eff =
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %A{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %A{v}"
                    FIO.Return v))
        let template eff = 
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %A{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %A{v}"
                    eff))
        repeat m template eff

    let internal spawnRecvProcess chanRecv chanSend name m =
        let eff = 
            FIO.Receive(chanRecv, fun v ->
                printfn $"%s{name} received: %A{v}"
                let value = v + 10
                FIO.Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %A{value}"
                    FIO.Return value))
        let template eff = 
            FIO.Receive(chanRecv, fun v ->
                printfn $"%s{name} received: %A{v}"
                let value = v + 10
                FIO.Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %A{value}"
                    eff))
        repeat m template eff

    type internal Process =
        { Name: string
          ChanSend: FIO.Channel<int>
          ChanRecv: FIO.Channel<int>
        }

    let internal getRecvChannel index (chans : FIO.Channel<int> list) =
        match index with
        | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
        | i                -> chans.Item (i - 1)

    let ring n m =
        let chans = [for _ in 1..n -> FIO.Channel<int>()]
        let mutable index = 0;
        let mutable processes = []

        for chan in chans do
            let proc = {Name = $"p{index}"; ChanSend = chan; ChanRecv = getRecvChannel index chans}
            processes <- List.append [proc] processes // in reverse order
            index <- index + 1

        let rec repeat n template eff chans m =
            if n = 1 then
                eff
            else 
                repeat (n - 1) template (template (n - 1) eff chans m) chans m
  
        let eff = let (px, py) = (List.item 0 processes, List.item 1 processes)
                  FIO.Concurrent(spawnRecvProcess py.ChanRecv py.ChanSend py.Name m, fun asyncPy -> 
                      FIO.Concurrent(spawnRecvProcess px.ChanRecv px.ChanSend px.Name m, fun asyncPx ->
                          FIO.Await(asyncPy, fun _ -> 
                              FIO.Await(asyncPx, fun res -> FIO.Return res))))

        ()
        
[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()

    let result = FIO.NaiveEval(Pingpong.intStrPingpongInf chanInt chanStr)
    printfn $"Result: %A{result}"

    0