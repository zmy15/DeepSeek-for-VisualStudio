
window.__renderMath=function(container){
    if(!container||typeof katex==='undefined')return;
    try{
        var mathEls=container.querySelectorAll('.math');
        for(var i=0;i<mathEls.length;i++){
            var el=mathEls[i];
            // 跳过已渲染的元素（KaTeX 渲染后仍保留 .math class，但 innerHTML 已替换）
            if(el.hasAttribute('data-katex-rendered'))continue;
            var tex=el.textContent.trim();
            if(!tex)continue;
            // Markdig 的 math renderer 会在内容外包一层 \(/\) 或 \[/\]
            // KaTeX 只需要纯 LaTeX 内容，必须剥离这些分隔符
            if((tex.startsWith('\\(',0)||tex.startsWith('\\[',0))&&(tex.endsWith('\\)')||tex.endsWith('\\]'))){
                tex=tex.substring(2,tex.length-2).trim();
            }
            var isDisplay=el.tagName==='DIV';
            try{
                katex.render(tex,el,{displayMode:isDisplay,throwOnError:false});
                el.setAttribute('data-katex-rendered','true');
            }catch(e2){
                // 渲染失败时保留原始 LaTeX 文本（可读性优于空白）
            }
        }
    }catch(e){}
};
