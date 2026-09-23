using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using UnityEngine;

// Links actual production pure-logic files. Adapter/RPC scheduling is explicitly modeled.
static class Program
{
    static MovementCommand Cmd(uint t)=>new(new Vector2(0,1),false,false,0,t);
    static void Require(bool ok,string message) { if(!ok) throw new Exception(message); }
    static void Main()
    {
        var output=new List<MovementCommand>();
        var q=new ServerInputQueue(); uint next=0;
        for(int serverTick=0;serverTick<300;serverTick++)
        { for(int j=0;j<3;j++)q.Enqueue(Cmd(++next)); q.Drain(MovementPredictionConfig.ServerMaxCatchUpPerTick,output); }
        Require(q.ConsumedTotal==900 && q.DroppedFuture==0,"Time-budget reproduction failed");
        Console.WriteLine($"TIME_BUDGET serverTicks=300 consumed={q.ConsumedTotal} simulatedSeconds={q.ConsumedTotal/30.0} wallSeconds=10 futureRejected={q.DroppedFuture}");

        var invalid=new ServerInputQueue();
        var bad=Cmd(1);bad.YawDelta=float.NaN;bad.PitchDelta=float.PositiveInfinity;bad.Move.x=float.NaN;
        invalid.Enqueue(bad);invalid.Drain(3,output);
        Require(output.Count==1 && float.IsNaN(output[0].YawDelta),"Invalid-input reproduction failed");
        Console.WriteLine("INVALID_INPUT non-finite command admitted to simulation queue");

        ObserverTimeline.Reset();
        var poses=new TimestampedPoseBuffer();
        poses.Push(new ObserverPose {ServerTick=100,LifeEpoch=1,Position=new Vector3(10,0,0)});
        ObserverTimeline.Observe(104,30,10); // Owner ACK / another player's healthy stream.
        double common=ObserverTimeline.Evaluate(10);
        poses.Evaluate(common,out var held);
        Require(poses.Starved && poses.ActualTick==100 && common>101,"Display mismatch reproduction failed");
        Console.WriteLine($"DISPLAY_MISMATCH submittedTick={common:F2} actualTargetTick={poses.ActualTick:F2} starved={poses.Starved} heldX={held.Position.x}");

        foreach(double rtt in new[]{.08,.15,.25})
        {
            ObserverTimeline.Reset();ObserverTimeline.Observe(300,30,10);
            double displayed=ObserverTimeline.Evaluate(10);
            // 300 was sampled one downlink flight before arrival; request travels one uplink flight.
            double receiveTick=300+rtt*30;
            Console.WriteLine($"WINDOW rttMs={rtt*1000:F0} displayAgeMs={(receiveTick-displayed)/30*1000:F0} accepted={ShotTimingPolicy.ValidDisplayTick(displayed,receiveTick,30)}");
        }

        var gun=new WeaponRuntime(30,90);
        Require(gun.TryConsumeRound(),"Initial shot failed");gun.StartCooldown(.1f);
        bool second=gun.TryConsumeRound();
        Require(!second,"Bunched shot reproduction failed");
        Console.WriteLine("BUNCHED_SHOTS two valid 100ms-spaced shots serviced in same server tick: first accepted, second rejected until Runtime.Tick advances");

        // Initial HardSnapTo(ack=0) sets localTick=0 without clearing the command buffer.
        var b=new PredictionBuffer();var old=Cmd(1);old.YawDelta=10;b.TryStore(old);
        var replacement=Cmd(1);replacement.YawDelta=-10;
        bool stored=b.TryStore(replacement);b.TryGetCommand(1,out var sent);
        Require(!stored && sent.YawDelta!=replacement.YawDelta,"Initial snap reproduction failed");
        Console.WriteLine($"INITIAL_SNAP reusedTick=1 predictedYaw={replacement.YawDelta} bufferedYaw={sent.YawDelta} storeAccepted={stored}");

        foreach(int delayTicks in new[]{1,3,6,8,10}) ModelReliableLink(delayTicks);
        Console.WriteLine("All audit reproduction assertions passed. This is NOT a Unity/FishNet integration test.");
    }
    static void ModelReliableLink(int oneWayTicks)
    {
        // Ordered, lossless 30Hz link. Adapter sends oldest 12 every two ticks, receives ACK every tick.
        // Models no position divergence/rebase; measures transport-window throughput only.
        var history=new PredictionBuffer();var queue=new ServerInputQueue();
        var uplink=new Queue<(int due,MovementCommand[] batch)>();
        var downlink=new Queue<(int due,uint ack)>();var output=new List<MovementCommand>();
        uint ack=0;long evictions=0;int peakLead=0;
        for(int t=1;t<=1800;t++)
        {
            while(downlink.TryPeek(out var a)&&a.due<=t)
            { downlink.Dequeue();if(a.ack>ack){ack=a.ack;history.PruneUpTo(ack);} }
            if(history.Count==128)evictions++;
            history.TryStore(Cmd((uint)t));
            if(t%2==0)uplink.Enqueue((t+oneWayTicks,history.CollectUnacked(ack,12).ToArray()));
            while(uplink.TryPeek(out var p)&&p.due<=t)
            { uplink.Dequeue();foreach(var c in p.batch)queue.Enqueue(c); }
            queue.Drain(3,output);downlink.Enqueue((t+oneWayTicks,queue.LastProcessedTick));
            peakLead=Math.Max(peakLead,t-(int)ack);
        }
        Console.WriteLine($"RELIABLE_MODEL rttMs={oneWayTicks*2/30.0*1000:F0} clientTicks=1800 serverAck={queue.LastProcessedTick} consumed={queue.ConsumedTotal} maxLead={peakLead} historyEvictions={evictions} skipped={queue.SkippedTicks} futureDrops={queue.DroppedFuture}");
    }
}
