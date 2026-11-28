var CB_COPY="{{copyPrefix}}";var CB_COPIED="{{copiedPrefix}}";
window.decorateCodeBlocks=function(container){
    if(!container)return;
    var pres=container.querySelectorAll('pre:not(.mermaid-block)');
    pres.forEach(function(pre){
        if(pre.querySelector('.copy-btn'))return;
        var code=pre.querySelector('code');
        if(!code)return;
        var lang='';
        if(code.className){
            var m=code.className.match(/language-(\w+)/);
            if(m)lang=m[1];
        }
        if(lang==='mermaid')return;
        if(lang){
            var label=document.createElement('span');
            label.className='code-lang';
            label.textContent=lang;
            pre.insertBefore(label,pre.firstChild);
        }
        if(window.hljs){
            try{window.hljs.highlightElement(code);}catch(e){}
        }
        // Copy button
        var copyBtn=document.createElement('button');
        copyBtn.className='copy-btn';
        // 文案取自注入的本地化字典（__codeBlockLabels），不再硬编码英文
        copyBtn.textContent=CB_COPY;copyBtn.style.fontFamily="'Segoe Fluent Icons','Segoe MDL2 Assets'";
        copyBtn.onclick=function(){
            var target=pre.querySelector('code')||pre;
            var text=target.innerText,ok=false;
            if(navigator.clipboard&&navigator.clipboard.writeText){
                navigator.clipboard.writeText(text);ok=true;
            }else{
                var ta=document.createElement('textarea');
                ta.value=text;ta.style.cssText='position:fixed;opacity:0';
                document.body.appendChild(ta);ta.select();
                try{document.execCommand('copy');ok=true;}catch(e){}
                document.body.removeChild(ta);
            }
            if(ok){copyBtn.textContent=CB_COPIED;copyBtn.style.background='#1a3a1a';copyBtn.style.color='#6cd96c';}
            setTimeout(function(){copyBtn.textContent=CB_COPY;copyBtn.style.fontFamily="'Segoe Fluent Icons','Segoe MDL2 Assets'";copyBtn.style.background='';copyBtn.style.color='';},2000);
        };
        pre.appendChild(copyBtn);
    });
};