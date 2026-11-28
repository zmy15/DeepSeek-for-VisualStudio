
document.addEventListener('wheel',function(e){
    if(!e.shiftKey)return;
    var pre=e.target.closest('pre');
    if(!pre||pre.scrollWidth<=pre.clientWidth)return;
    pre.scrollLeft+=e.deltaY>0?80:-80;
    e.preventDefault();
},{passive:false});
