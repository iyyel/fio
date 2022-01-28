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

let processRing processCount roundCount =
    let par(effA, effB) = FIO.Concurrent(effA, fun asyncA ->
                              FIO.Concurrent(effB, fun asyncB ->
                                  FIO.Await(asyncA, fun _ ->
                                      FIO.Await(asyncB, fun _ ->
                                          FIO.Return 0))))

    let getRecvChan index (chans : FIO.Channel<int> list) =
        match index with
        | i when i - 1 < 0 -> chans.Item (List.length chans - 1)
        | i                -> chans.Item (i - 1)

    let rec createProcesses chans allChans index acc =
        match chans with
        | []    -> acc
        | c::cs -> let proc = {Name = $"p{index}"; ChanSend = c; ChanRecv = getRecvChan index allChans}
                   createProcesses cs allChans (index + 1) (acc @ [proc])

    let rec createProcessRing procs index m = 
        match procs with
        | pa::pb::[] when index = 0 -> par(createSendProcess pa.ChanSend pa.ChanRecv 0 pa.Name m, createRecvProcess pb.ChanRecv pb.ChanSend pb.Name m)
        | pa::pb::[]                -> par(createRecvProcess pa.ChanRecv pa.ChanSend pa.Name m, createRecvProcess pb.ChanRecv pb.ChanSend pb.Name m)
        | p::ps when index = 0      -> par(createSendProcess p.ChanSend p.ChanRecv 0 p.Name m, createProcessRing ps (index + 1) m)
        | p::ps                     -> par(createRecvProcess p.ChanRecv p.ChanSend p.Name m, createProcessRing ps (index + 1) m)
        | _                         -> failwith "createRing failed!"

    let chans = [for _ in 1..processCount -> FIO.Channel<int>()]

    let processes = createProcesses chans chans 0 []

    createProcessRing processes 0 roundCount