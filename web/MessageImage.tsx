import React, {useState} from 'react';
export function MessageImage({url,sticker,alt}:{url:string;sticker:boolean;alt:string}){
  const [failed,setFailed]=useState(false);
  if(failed)return <p className="media-missing" role="status">{sticker?'המדבקה לא זמינה או שהדפדפן לא הצליח להציג אותה.':'התמונה לא זמינה או שהדפדפן לא הצליח להציג אותה.'}</p>;
  return <a className="media-image" href={url} target="_blank" rel="noreferrer"><img src={url} loading="lazy" alt={alt} onError={()=>setFailed(true)}/></a>;
}
