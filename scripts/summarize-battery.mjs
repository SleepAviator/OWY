import fs from 'node:fs';
import path from 'node:path';

const filename=process.argv[2];
if(!filename)throw new Error('Pass the completed battery comparison JSON');
const comparison=JSON.parse(fs.readFileSync(filename,'utf8'));
const quick=comparison.profile==='quick';
if(comparison.status!=='complete'||comparison.phases.length!==(quick?2:4))throw new Error('All planned phases must be completed');
const dir=path.dirname(filename);
const one=value=>Array.isArray(value)?value.length===1?value[0]:null:value;
const watts=b=>b?.Discharging&&!b.PowerOnline&&!b.Charging&&b.DischargeRate>0&&b.DischargeRate<200000?b.DischargeRate/1000:null;
const capacity=b=>Number.isFinite(b?.RemainingCapacity)&&b.RemainingCapacity>0&&b.RemainingCapacity<200000?b.RemainingCapacity:null;
const mean=values=>values.reduce((a,b)=>a+b,0)/values.length;
const phases=comparison.phases.map(phase=>{
    const data=JSON.parse(fs.readFileSync(path.join(dir,phase.report),'utf8'));
    const before=one(data.battery_before);
    const observations=[{t:0,watts:watts(before),capacity:capacity(before)},...data.samples.map(s=>{
        const b=one(s.battery);return {t:s.elapsed_seconds,watts:watts(b),capacity:capacity(b)};
    })];
    let integral=0,covered=0;
    for(let i=1;i<observations.length;i++){
        const a=observations[i-1],b=observations[i];const dt=b.t-a.t;
        if(dt<0)throw new Error('Sample times moved backward');
        if(a.watts!==null&&b.watts!==null){integral+=dt*(a.watts+b.watts)/2;covered+=dt;}
    }
    if(covered/data.seconds<.95)throw new Error('Insufficient valid discharge coverage');
    const last=observations.at(-1);
    const drop=observations[0].capacity!==null&&last.capacity!==null?observations[0].capacity-last.capacity:null;
    return {index:phase.index,mode:phase.mode,seconds:data.seconds,samples:data.samples.length,
        time_weighted_discharge_watts:integral/covered,legacy_unweighted_discharge_watts:data.mean_discharge_watts,
        coverage_fraction:covered/data.seconds,capacity_drop_mwh:drop,capacity_derived_watts:drop===null?null:drop*3.6/data.seconds,
        mean_system_cpu_percent:data.system_cpu_percent,owy_one_core_percent:data.one_core_percent,
        nvidia_states:[...new Set(data.samples.flatMap(s=>(s.nvidia_device_power??[]).map(g=>g.state)))],
        brightness_before:phase.before.brightness,brightness_after:phase.after.brightness,
        start:phase.before.at,end:phase.after.at,report:phase.report,
        background_top:data.top_background_cpu,
        raw_discharge_range_watts:{min:Math.min(...observations.filter(s=>s.watts!==null).map(s=>s.watts)),max:Math.max(...observations.filter(s=>s.watts!==null).map(s=>s.watts))}};
});
if(phases.map(p=>p.mode).join('/')!==(quick?'disabled/enabled':'enabled/disabled/disabled/enabled'))throw new Error('Unexpected phase order');
const summarize=mode=>{
    const rows=phases.filter(p=>p.mode===mode);const seconds=rows.reduce((s,p)=>s+p.seconds,0);
    return {seconds,discharge_watts:rows.reduce((s,p)=>s+p.time_weighted_discharge_watts*p.seconds,0)/seconds,
        capacity_derived_watts:rows.every(p=>p.capacity_derived_watts!==null)?rows.reduce((s,p)=>s+p.capacity_derived_watts*p.seconds,0)/seconds:null,
        system_cpu_percent:rows.reduce((s,p)=>s+p.mean_system_cpu_percent*p.seconds,0)/seconds,
        owy_one_core_percent:rows.reduce((s,p)=>s+p.owy_one_core_percent*p.seconds,0)/seconds};
};
const enabled=summarize('enabled'),disabled=summarize('disabled');
const differences=quick?[phases[1].time_weighted_discharge_watts-phases[0].time_weighted_discharge_watts]:[phases[0].time_weighted_discharge_watts-phases[1].time_weighted_discharge_watts,phases[3].time_weighted_discharge_watts-phases[2].time_weighted_discharge_watts];
const result={version:1,source:path.basename(filename),build:comparison.build,order:comparison.order,phases,enabled,disabled,
    enabled_minus_disabled_watts:enabled.discharge_watts-disabled.discharge_watts,
    capacity_enabled_minus_disabled_watts:enabled.capacity_derived_watts!==null&&disabled.capacity_derived_watts!==null?enabled.capacity_derived_watts-disabled.capacity_derived_watts:null,
    adjacent_pair_differences_watts:differences,pair_mean_difference_watts:mean(differences),
    battery_telemetry_method:'Time-weighted trapezoid integration, including the pre-phase reading. Tiny final intervals do not get the same weight as ten-second intervals. Capacity endpoints are an independent, coarse cross-check; mWh units require valid non-relative battery IOCTL capabilities.',
    settings_preserved:comparison.settings_preserved,codex_config_preserved:comparison.codex_config_preserved,owy_restored:comparison.owy_restored,
    limitations:(quick?'Two short serial blocks, one pair: only a quick estimate. Firmware lag and quantization can obscure small differences. ':'Four serial blocks, two adjacent pairs. ')+'Existing apps left running. No confidence bound or full-discharge runtime is established. Windows most-recent GPU power state is not proof of physical sleep. Observer activity exists in both conditions.'};
const output=path.join(dir,path.basename(filename).replace('-comparison.json','-summary.json'));
fs.writeFileSync(output,JSON.stringify(result,null,2)+'\n');
console.log(JSON.stringify({output,enabled,disabled,difference_watts:result.enabled_minus_disabled_watts,capacity_difference_watts:result.capacity_enabled_minus_disabled_watts,pair_differences_watts:differences,phases:phases.map(({index,mode,time_weighted_discharge_watts,capacity_derived_watts,mean_system_cpu_percent,owy_one_core_percent,nvidia_states})=>({index,mode,time_weighted_discharge_watts,capacity_derived_watts,mean_system_cpu_percent,owy_one_core_percent,nvidia_states}))},null,2));
