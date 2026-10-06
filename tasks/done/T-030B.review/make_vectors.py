from pathlib import Path
import json
p=Path(__file__).parent
def line(text,bounds=None):return {'text':text,'bounds':bounds}
def case(name,lines,expected,layout='MergeParagraphs'):return {'name':name,'layout':layout,'lines':lines,'expected':expected}
rows=[
 case('empty',[],''),
 case('keep_edges',[line(' a '),line(' b ')],' a \n b ','KeepLines'),
 case('keep_internal',[line('a\r\nb'),line('c')],'a\r\nb\nc','KeepLines'),
 case('latin',[line('hello'),line('world')],'hello world'),
 case('han',[line('中文'),line('测试')],'中文测试'),
 case('supplementary',[line('𠀀'),line('中')],'𠀀中'),
 case('hangul',[line('한'),line('글')],'한 글'),
 case('mixed',[line('中'),line('word')],'中 word'),
 case('hyphen',[line('trans-'),line('lation')],'translation'),
 case('number-hyphen',[line('123-'),line('word')],'123- word'),
 case('sentence',[line('done.'),line('next')],'done.\nnext'),
 case('closing',[line('done!”'),line('next')],'done!”\nnext'),
 case('list',[line('heading'),line('- item')],'heading\n- item'),
 case('decimal',[line('value'),line('1.25')],'value 1.25'),
 case('numbered',[line('heading'),line('1. item')],'heading\n1. item'),
 case('blank-paragraph',[line('a'),line(''),line(''),line('b')],'a\n\n\nb'),
 case('internal-atomic',[line('alpha\nbeta'),line('gamma')],'alpha\nbeta gamma'),
 case('threshold-equal',[line('a',[0,0,10,10]),line('b',[0,25,10,10])],'a b'),
 case('threshold-over',[line('a',[0,0,10,10]),line('b',[0,26,10,10])],'a\nb'),
 case('mean-height-below',[line('a',[0,0,10,10]),line('b',[0,32,10,20])],'a b'),
 case('mean-height-over',[line('a',[0,0,10,10]),line('b',[0,33,10,20])],'a\nb'),
 case('whole-set-mean',[line('a',[0,0,10,10]),line('b',[0,30,10,10]),line('c',[0,55,10,100])],'a b c'),
 case('overlap',[line('a',[0,50,10,20]),line('b',[0,55,10,10])],'a b'),
 case('partial',[line('a',[0,0,10,10]),line('b')],'a b'),
 case('invalid-negative',[line('a',[-1,0,10,10]),line('b',[0,100,10,10])],'a b'),
 case('invalid-zero',[line('a',[0,0,0,10]),line('b',[0,100,10,10])],'a b'),
 case('overflow-safe',[line('a',[0,2147483647,10,100]),line('b',[0,2147483647,10,100])],'a b'),
]
(p/'normalizer-vectors.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print(f'Wrote {len(rows)} fixed expected vectors. No product execution.')
