module Ring

open FSharp.FIO

type private Process =
    { Name: string
      ChanSend: FIO.Channel<int>
      ChanRecv: FIO.Channel<int>
    }

let private createSendProcess chanSend chanRecv value name m =
    let rec create n = 
        if n = 1 then
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %A{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %A{v}"
                    FIO.Return 0))
        else 
            FIO.Send(value, chanSend, fun () ->
                printfn $"%s{name} sent: %A{value}"
                FIO.Receive(chanRecv, fun v ->
                    printfn $"%s{name} received: %A{v}"
                    create (n - 1)))
    create m

let private createRecvProcess chanRecv chanSend name m =
    let rec create n =
        if n = 1 then
            FIO.Receive(chanRecv, fun v ->
                printfn $"%s{name} received: %A{v}"
                let value = v + 10
                FIO.Send(value, chanSend, fun () ->
                    printfn $"%s{name} sent: %A{value}"
                    FIO.Return 0))
        else
            FIO.Receive(chanRecv, fun v ->
                     printfn $"%s{name} received: %A{v}"
                     let value = v + 10
                     FIO.Send(value, chanSend, fun () ->
                         printfn $"%s{name} sent: %A{value}"
                         create (n - 1)))
    create m

let run processCount roundCount =
    let getRecvChannel index (chans : FIO.Channel<int> list) =
        match index with
        | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
        | i                -> chans.Item (i - 1)

    let rec createProcesses chans allChans index acc =
        match chans with
        | []    -> acc
        | c::cs -> let proc = {Name = $"p{index}"; ChanSend = c; ChanRecv = getRecvChannel index allChans}
                   createProcesses cs allChans (index + 1) (acc @ [proc])

    let chans = [for _ in 1..processCount -> FIO.Channel<int>()]

    let processes = createProcesses chans chans 0 []

    printfn "%A" processes
    
    ()