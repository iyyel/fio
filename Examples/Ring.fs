module Ring

open FSharp.FIO

let rec private repeat n template eff =
    if n = 1 then
        eff
    else 
        repeat (n - 1) template (template eff)

let private spawnSendProcess chanSend chanRecv value name m =
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

let private spawnRecvProcess chanRecv chanSend name m =
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

type private Process =
    { Name: string
      ChanSend: FIO.Channel<int>
      ChanRecv: FIO.Channel<int>
    }

let private getRecvChannel index (chans : FIO.Channel<int> list) =
    match index with
    | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
    | i                -> chans.Item (i - 1)

let run n m =
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